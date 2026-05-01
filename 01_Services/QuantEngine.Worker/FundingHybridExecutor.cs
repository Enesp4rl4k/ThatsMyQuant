using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Application.Strategy.ProMode;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Notifications;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Hybrid funding-alpha executor — bridges the validated FundingAlphaBacktestRunner with REAL
/// OKX demo execution. Each tick: re-runs the backtest to get the runner's "live signal" on
/// the latest bar, maintains a per-symbol position state machine, and (in live mode) places
/// real OKX market orders via <see cref="IExchangeService.ExecuteOrderAsync"/>.
///
/// **Barbell tiers:** A single instance is bound to ONE tier ("Short" or "Long" or legacy "")
/// at construction. Two registrations in DI (one per tier) form the barbell portfolio:
///  - SHORT tier: low threshold (0.3bp), small size (0.5%), high lev (5x), short hold (4h),
///    cap 8 positions. Frequent small bets.
///  - LONG tier: high threshold (1.0bp), big size (5-15%), low lev (1-2x), long hold (16h+),
///    cap 2-3 positions. Rare conviction bets.
/// Config sections: <c>Funding:Short:*</c>, <c>Funding:Long:*</c>. If the tiered key is
/// missing the executor falls back to the legacy flat <c>Funding:*</c> keys for backward compat.
///
/// Three modes via <c>Funding:{Tier}:HybridMode</c> config:
///   - <c>off</c>     : disabled (legacy <see cref="FundingAlphaPaperHostedService"/> takes over).
///   - <c>dry-run</c> : evaluates signals + logs WHAT WOULD HAPPEN, no real orders. Default + safe.
///   - <c>live</c>    : same as dry-run + calls ExecuteOrderAsync on entries/exits.
///
/// State persisted to JSON so positions survive restarts (per-tier file).
/// </summary>
public sealed class FundingHybridExecutor : BackgroundService
{
    /// <summary>
    /// Static cross-instance registry so HybridReconciliationHostedService can call back
    /// into the right tier's executor when it detects a GHOST (internal state has a
    /// position that no longer exists on OKX — usually because OKX-side bracket TP/SL
    /// fired between our ticks). Without this hook the executor never learns the
    /// position is gone, the slot stays "occupied" forever, and reconciliation spams
    /// Telegram with the same GHOST alert every 120s.
    /// Key: state-file basename (matches reconciliation's sourceTag), Value: executor instance.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FundingHybridExecutor> _instances =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reconciliation calls this when it detects a GHOST. Routes to the right tier
    /// executor by source tag substring match. No-op if the tier isn't registered
    /// (e.g. pairs reconciliation calling into funding registry).
    /// </summary>
    public static bool TryHandleGhostFromReconciliation(string sourceTagFromReconcile, string symbol)
    {
        // sourceTag examples: "Funding[funding_short_state]", "Funding[funding_long_state]"
        foreach (var (key, instance) in _instances)
        {
            if (sourceTagFromReconcile.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return instance.RemoveGhostPosition(symbol);
            }
        }
        return false;
    }

    /// <summary>
    /// Symbol-level circuit breaker feeder. Called from BOTH worker-initiated closes
    /// (HandleCloseAsync) AND bracket-fired closes (RemoveGhostPosition background task).
    /// Negative PnL → increment consecutive-loss counter; positive → reset to 0. When the
    /// symbol crosses either threshold (consecutive losses OR daily loss %), it's added
    /// to the blacklist and entry-blocked until UTC midnight reset.
    /// </summary>
    private void RecordSymbolPnl(string symbol, decimal netPnl)
    {
        var basis = _portfolio.InitialBalance > 0m ? _portfolio.InitialBalance : 100_000m;
        bool shouldBlacklist = false;
        int consecLosses = 0;
        decimal symbolPnl = 0m;
        decimal lossPct = 0m;

        lock (_symbolLock)
        {
            if (!_symbolDailyPnl.ContainsKey(symbol)) _symbolDailyPnl[symbol] = 0m;
            _symbolDailyPnl[symbol] += netPnl;

            if (netPnl < 0m)
                _symbolConsecutiveLosses[symbol] = _symbolConsecutiveLosses.GetValueOrDefault(symbol) + 1;
            else if (netPnl > 0m)
                _symbolConsecutiveLosses[symbol] = 0;

            consecLosses = _symbolConsecutiveLosses.GetValueOrDefault(symbol);
            symbolPnl = _symbolDailyPnl[symbol];
            lossPct = symbolPnl < 0m ? -symbolPnl / basis : 0m;

            if (!_symbolBlacklist.Contains(symbol) &&
                (consecLosses >= _symbolMaxConsecutiveLosses || lossPct >= _symbolMaxDailyLossPctBasis))
            {
                _symbolBlacklist.Add(symbol);
                shouldBlacklist = true;
            }
        }

        if (shouldBlacklist)
        {
            _logger.LogCritical(
                "SYMBOL BLACKLIST [{Tier}]: {Symbol} blocked for rest of UTC day. consecLosses={Cl}/{ClMax} dailyPnL=${Pnl:F2} ({LossPct:P2}/{LossMax:P2})",
                _tierName, symbol, consecLosses, _symbolMaxConsecutiveLosses, symbolPnl, lossPct, _symbolMaxDailyLossPctBasis);
            _ = _alerter?.SendAsync($"symbol_blacklist_{symbol}", AlertSeverity.Warning,
                "Symbol Blacklisted",
                $"`{symbol}` blocked from new entries today.\nConsecutive losses: {consecLosses}/{_symbolMaxConsecutiveLosses}\nDaily PnL: ${symbolPnl:F2} ({lossPct:P2} of basis)\nResets at UTC midnight.",
                CancellationToken.None);
        }
    }

    private bool RemoveGhostPosition(string symbol)
    {
        if (!_openPositions.TryGetValue(symbol, out var existing)) return false;

        _openPositions.Remove(symbol);
        _opportunisticSymbols.Remove(symbol);
        _notionalGuard?.Release($"Funding:{_tierName}", symbol);
        try { SaveState(); } catch (Exception ex) { _logger.LogWarning(ex, "GHOST cleanup save-state failed for {Symbol}", symbol); }

        // Background: fetch the bracket-fired exit fill from OKX and publish a CLOSED
        // notification so Telegram + AI review fire just like a normal close. Without
        // this, the operator only ever sees "🟢 OPENED" on Telegram and never knows
        // when a bracket-protected position closed or with what PnL.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var reader = scope.ServiceProvider.GetService<IExchangePositionReader>();
                ExchangeFillSnapshot? fill = null;
                // 60-min window: ghost-cleanup commonly fires after a worker restart where
                // the OKX-side bracket may have triggered tens of minutes ago. The default
                // 5-min window in GetLastFillAsync is for live fast-path recovery only.
                // Without the wider window the fill (and its real fee) is lost to the cutoff
                // and we silently fall back to the assumed cost — exactly the regression
                // observed in trade #53 (entry == exit == 55.23, RealFeePaid null).
                if (reader is not null) fill = await reader.GetLastFillAsync(symbol, lookbackMinutes: 60).ConfigureAwait(false);

                var exitPrice = fill?.Price ?? existing.EntryPrice;
                var exitQty   = fill?.Quantity > 0m ? fill.Quantity : existing.Qty;
                var exitTime  = fill?.FillTimeUtc ?? DateTime.UtcNow;
                // PnL: long → exit-entry; short → entry-exit
                var grossPnl = existing.Side == TradeSide.Buy
                    ? (exitPrice - existing.EntryPrice) * exitQty
                    : (existing.EntryPrice - exitPrice) * exitQty;
                // Cost: prefer real venue-reported fee from the exit fill (doubled to approximate
                // entry+exit round-trip — fills-history harvest 2026-04-30 showed legs are
                // symmetric on OKX VIP-0 demo). Fall back to the configured rate when the fill
                // API didn't return a fee value.
                decimal realRoundTripFee = fill?.FeePaid > 0m
                    ? fill.FeePaid * 2m
                    : 0m;
                var costEstimate = realRoundTripFee > 0m
                    ? realRoundTripFee
                    : exitPrice * exitQty * _config.RoundTripCostRate;
                var netPnl = grossPnl - costEstimate;
                var pnlPct = existing.EntryPrice > 0m ? (netPnl / (existing.EntryPrice * exitQty)) * 100m : 0m;

                _logger.LogWarning(
                    "FundingHybrid[{Tier}] GHOST CLOSED {Symbol} {Side} entry={Entry:F4} exit={Exit:F4} PnL={Pnl:F2} ({Pct:F2}%) reason=OKX bracket fired",
                    _tierName, symbol, existing.Side, existing.EntryPrice, exitPrice, netPnl, pnlPct);

                // Mirror the bracket-fired close into PaperPortfolio so the dashboard
                // snapshot stays consistent with worker-initiated closes.
                var ghostClosed = new PaperPosition
                {
                    Symbol = symbol,
                    Side = existing.Side,
                    EntryPrice = existing.EntryPrice,
                    CurrentPrice = exitPrice,
                    Quantity = exitQty,
                    EntryTime = existing.EntryTimeUtc,
                    ExitTime = exitTime,
                    RealizedPnL = netPnl,
                    ExitReason = "BracketFired",
                    Reasoning = $"OKX bracket TP/SL fired (ghost cleanup). entryFunding={existing.EntryFunding:F5}",
                    StrategyTag = _strategyTag,
                    RegimeTag = "FundingMeanReversion",
                    ExecutionMode = "OKX_DEMO",
                    PromotionStage = "Paper",
                    NetFunding = existing.EntryFunding,
                    RealFeePaid = realRoundTripFee > 0m ? realRoundTripFee : null,
                };
                lock (_portfolio)
                {
                    _portfolio.OpenPositions.RemoveAll(p => p.Symbol == symbol && p.EntryTime == existing.EntryTimeUtc);
                    _portfolio.ClosedPositions.Add(ghostClosed);
                    _portfolio.Cash += netPnl;
                    _portfolio.DailyPnL += netPnl;
                    _portfolio.AppendEquity(_portfolio.TotalEquity);
                }
                _persistence?.RequestWrite(_portfolio);

                var note = new PositionClosedNotification(
                    Symbol: symbol,
                    StrategyTag: _strategyTag,
                    RegimeTag: $"funding={existing.EntryFunding:F5}",
                    ExitPolicyTag: "OKX-Bracket",
                    ExitReason: "BracketFired",
                    ExecutionMode: "OKX_DEMO",
                    Side: existing.Side,
                    EntryPrice: existing.EntryPrice,
                    ExitPrice: exitPrice,
                    Quantity: exitQty,
                    NetPnl: netPnl,
                    NetPnlPercent: pnlPct,
                    EntryTimeUtc: existing.EntryTimeUtc,
                    ExitTimeUtc: exitTime,
                    CorrelationId: existing.ClientOrderId);
                await PublishNotificationAsync(note).ConfigureAwait(false);

                // Update tier daily PnL + global breaker so bracket exits count toward
                // the same risk gates as worker-initiated closes.
                _dailyRealizedPnL += netPnl;
                _globalBreaker?.RecordRealizedPnl($"Funding:{_tierName}", netPnl,
                    _portfolio.InitialBalance > 0m ? _portfolio.InitialBalance : 100_000m);

                // Symbol-level circuit (Fix 3): track losses, blacklist after threshold.
                RecordSymbolPnl(symbol, netPnl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GHOST cleanup CLOSED-notification publish failed for {Symbol}", symbol);
            }
        });

        _logger.LogWarning(
            "FundingHybrid[{Tier}] GHOST cleanup: {Symbol} removed from internal state (OKX bracket TP/SL fired). Slot freed. Fetching exit fill for CLOSED notification…",
            _tierName, symbol);
        return true;
    }

    private readonly ILogger<FundingHybridExecutor> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly PaperPortfolio _portfolio;
    // [TCA persistence] symbol+entryTime → SQLite Trade.Id for close-time row update.
    private readonly Dictionary<string, Guid> _tradeIdByLifecycle = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _tierName;          // "Short", "Long", or "" (legacy/single-tier)
    private readonly string _strategyTag;       // "FundingAlpha-Hybrid-Short", "...-Long", "FundingAlpha-Hybrid"
    private readonly TimeSpan _interval;
    private readonly string _timeframe;
    private readonly int _candleLookback;
    private readonly Dictionary<string, decimal> _capitalPerSymbol;
    private readonly FundingAlphaConfig _config;
    private readonly HybridMode _mode;
    private readonly string _statePath;
    private readonly Dictionary<string, OpenPosition> _openPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IRealtimeAlerter? _alerter;
    private decimal _dailyRealizedPnL = 0m;
    private DateTime _dailyResetUtc = DateTime.UtcNow.Date;
    private bool _circuitBreakerTripped = false;
    private readonly decimal _circuitBreakerLossPctOfBasis;
    // Opportunistic 25-symbol funding scan (FundingOpportunityScanner) — picks up symbols
    // outside the static AllocationCsv when their |funding| crosses 2× our entry threshold.
    private readonly FundingOpportunityScanner? _opportunityScanner;
    private readonly bool _opportunisticEnabled;
    private readonly int _opportunisticMaxPositions;
    private readonly decimal _opportunisticSizeFraction;
    private readonly decimal _opportunisticThresholdMultiplier;
    private readonly HashSet<string> _opportunisticSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxStaticPositions; // hard cap on simultaneously held STATIC positions for this tier

    private readonly GlobalCircuitBreaker? _globalBreaker;
    private readonly NotionalExposureGuard? _notionalGuard;
    private readonly PaperPortfolioPersistenceService? _persistence;

    // Symbol-level circuit breaker (2026-04-27).
    // The tier-wide breaker only trips at ~$30 daily loss; one chronically losing
    // symbol can repeatedly take SL hits and bleed without ever crossing the tier
    // threshold. This per-symbol gate blacklists a symbol after it accumulates either
    // N consecutive SL/loss exits OR a configurable % of basis in losses.
    // Reset at UTC midnight along with daily PnL.
    private readonly Dictionary<string, decimal> _symbolDailyPnl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _symbolConsecutiveLosses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _symbolBlacklist = new(StringComparer.OrdinalIgnoreCase);
    // Guards _symbolDailyPnl / _symbolConsecutiveLosses / _symbolBlacklist — accessed from
    // both the main tick loop and the GHOST cleanup Task.Run background continuation.
    private readonly object _symbolLock = new();
    private int _symbolMaxConsecutiveLosses;     // e.g. 3
    private decimal _symbolMaxDailyLossPctBasis; // e.g. 0.005 = 0.5% basis

    public FundingHybridExecutor(
        ILogger<FundingHybridExecutor> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null,
        FundingOpportunityScanner? opportunityScanner = null,
        string tierName = "",
        GlobalCircuitBreaker? globalBreaker = null,
        NotionalExposureGuard? notionalGuard = null,
        PaperPortfolioPersistenceService? persistence = null)
    {
        _globalBreaker = globalBreaker;
        _notionalGuard = notionalGuard;
        _persistence = persistence;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _portfolio = portfolio;
        _alerter = alerter;
        _opportunityScanner = opportunityScanner;
        _tierName = tierName ?? string.Empty;
        _strategyTag = string.IsNullOrEmpty(_tierName)
            ? "FundingAlpha-Hybrid"
            : $"FundingAlpha-Hybrid-{_tierName}";

        _circuitBreakerLossPctOfBasis = ParseDecimal(configuration["Hybrid:CircuitBreakerDailyLossPct"], 0.03m);

        // Symbol-level breaker config (tier-scoped, falls back to flat keys).
        _symbolMaxConsecutiveLosses = int.TryParse(Cfg("SymbolMaxConsecutiveLosses"), out var smc) && smc > 0 ? smc : 3;
        _symbolMaxDailyLossPctBasis = ParseDecimal(Cfg("SymbolMaxDailyLossPctBasis"), 0.005m);

        // Opportunistic scanner config — tier-scoped. Long tier uses larger thresh multiplier,
        // Short tier already runs aggressively so it stays opt-in per tier.
        _opportunisticEnabled = bool.TryParse(Cfg("Opportunistic:Enabled"), out var oe) && oe;
        _opportunisticMaxPositions = int.TryParse(Cfg("Opportunistic:MaxPositions"), out var omp) && omp > 0 ? omp : 3;
        _opportunisticSizeFraction = ParseDecimal(Cfg("Opportunistic:SizeFraction"), 0.02m);
        _opportunisticThresholdMultiplier = ParseDecimal(Cfg("Opportunistic:ThresholdMultiplier"), 2.0m);

        var modeRaw = Cfg("HybridMode") ?? "off";
        _mode = modeRaw.Trim().ToLowerInvariant() switch
        {
            "live" => HybridMode.Live,
            "dry-run" or "dryrun" => HybridMode.DryRun,
            _ => HybridMode.Off
        };

        _timeframe = Cfg("Timeframe") ?? "1h";
        _candleLookback = int.TryParse(Cfg("CandleLookback"), out var cl) && cl > 0 ? cl : 300;
        var intervalSeconds = int.TryParse(Cfg("IntervalSeconds"), out var iv) && iv > 0 ? iv : 1800;
        _interval = TimeSpan.FromSeconds(intervalSeconds);
        _maxStaticPositions = int.TryParse(Cfg("MaxConcurrentPositions"), out var mcp) && mcp > 0 ? mcp : int.MaxValue;

        _capitalPerSymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var allocCsv = Cfg("AllocationCsv");
        if (!string.IsNullOrWhiteSpace(allocCsv))
        {
            foreach (var entry in allocCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && decimal.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var raw))
                {
                    _capitalPerSymbol[parts[0]] = raw > 1.0m ? raw / 100_000m : raw;
                }
            }
        }

        _config = new FundingAlphaConfig(
            EntryThreshold: ParseDecimal(Cfg("EntryThresholdRate"), 0.0001m),
            ExitThreshold: ParseDecimal(Cfg("ExitThresholdRate"), 0.00005m),
            TakeProfitPct: ParseDecimal(Cfg("TakeProfitPct"), 0.02m),
            StopLossPct: ParseDecimal(Cfg("StopLossPct"), 0.015m),
            MaxHoldingBars: int.TryParse(Cfg("MaxHoldingBars"), out var mh) && mh > 0 ? mh : 16,
            BaseSizeFraction: ParseDecimal(Cfg("BaseSizeFraction"), 0.05m),
            MaxSizeFraction: ParseDecimal(Cfg("MaxSizeFraction"), 0.15m),
            // 2026-04-28: now config-driven so paper/live can truth-tell real OKX
            // fees (~28bp post-maker entry + taker exit). Default 8bp was optimistic
            // and made the simulated PnL look stronger than live would actually be.
            RoundTripCostRate: ParseDecimal(Cfg("RoundTripCostRate"), 0.0008m));

        var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant");
        Directory.CreateDirectory(stateDir);
        // Per-tier state file. Falls back to legacy "funding_hybrid_state.json" if tier-scoped
        // file doesn't exist AND we're the legacy/Long tier (so existing paper continues).
        var stateFileName = string.IsNullOrEmpty(_tierName)
            ? "funding_hybrid_state.json"
            : $"funding_{_tierName.ToLowerInvariant()}_state.json";
        _statePath = Path.Combine(stateDir, stateFileName);
        if (!File.Exists(_statePath) && _tierName.Equals("Long", StringComparison.OrdinalIgnoreCase))
        {
            var legacyPath = Path.Combine(stateDir, "funding_hybrid_state.json");
            if (File.Exists(legacyPath))
            {
                _logger.LogInformation("FundingHybridExecutor[{Tier}]: migrating state from legacy path {Legacy} → {New}", _tierName, legacyPath, _statePath);
                File.Copy(legacyPath, _statePath);
            }
        }
        LoadState();

        // Register in static instance map keyed by state-file basename so reconciliation
        // can route GHOST cleanup callbacks back to this executor.
        var registryKey = Path.GetFileNameWithoutExtension(stateFileName);
        _instances[registryKey] = this;
    }

    /// <summary>
    /// Tier-aware config lookup with backward-compat fallback. Reads
    /// <c>Funding:{Tier}:{key}</c> first, then <c>Funding:{key}</c>. This lets existing flat
    /// config keep working while new tier-scoped sections override per tier.
    /// </summary>
    private string? Cfg(string key)
    {
        if (!string.IsNullOrEmpty(_tierName))
        {
            var tiered = _configuration[$"Funding:{_tierName}:{key}"];
            if (!string.IsNullOrWhiteSpace(tiered)) return tiered;
        }
        return _configuration[$"Funding:{key}"];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_mode == HybridMode.Off)
        {
            _logger.LogInformation("FundingHybridExecutor[{Tier}] disabled (HybridMode=off).", _tierName);
            return;
        }

        _logger.LogWarning(
            "FundingHybridExecutor[{Tier}] starting in {Mode} mode. Symbols={Syms} Interval={Interval}s Threshold={Threshold:F5} TF={TF} Hold={Hold}b Size={Size:P2} (open={Open}) MaxPositions={MaxPos} Opportunistic={Opp} (max={OppMax} size={OppSize:P1} thrMult={OppMul:F1}x)",
            string.IsNullOrEmpty(_tierName) ? "single" : _tierName,
            _mode, string.Join(",", _capitalPerSymbol.Keys), _interval.TotalSeconds, _config.EntryThreshold,
            _timeframe, _config.MaxHoldingBars, _config.BaseSizeFraction, _openPositions.Count,
            _maxStaticPositions == int.MaxValue ? "∞" : _maxStaticPositions.ToString(),
            _opportunisticEnabled && _opportunityScanner is not null ? "ON" : "OFF",
            _opportunisticMaxPositions, _opportunisticSizeFraction, _opportunisticThresholdMultiplier);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try
                {
                    await ProcessAllSymbolsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FundingHybridExecutor tick failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAllSymbolsAsync(CancellationToken ct)
    {
        var basis = _portfolio.InitialBalance;
        if (basis <= 0m)
        {
            _logger.LogWarning("FundingHybrid[{Tier}] skipped: PaperPortfolio.InitialBalance=0 (waiting for OKX balance sync).", _tierName);
            return;
        }

        // Macro event pause — block NEW entries within event window. Existing positions still
        // managed (close/exit signals fire). Auto-resumes after the post-event window.
        var macroPause = MacroEventCalendarService.Current;
        if (macroPause.IsPaused)
        {
            _logger.LogWarning(
                "FundingHybrid[{Tier}] macro-paused: {Event} {Phase} (T{Mins:+0;-0}m). New entries SUPPRESSED, exits still managed.",
                _tierName, macroPause.EventName, macroPause.Phase, macroPause.MinutesUntilEvent);
        }

        using var scope = _serviceProvider.CreateScope();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();
        var runner = scope.ServiceProvider.GetRequiredService<FundingAlphaBacktestRunner>();
        var exchange = scope.ServiceProvider.GetService<IExchangeService>();

        if (_mode == HybridMode.Live && exchange is null)
        {
            _logger.LogError("FundingHybrid LIVE mode requires IExchangeService — none registered. Tick aborted.");
            return;
        }

        foreach (var (symbol, weight) in _capitalPerSymbol)
        {
            if (ct.IsCancellationRequested) return;
            // Per-tier hard cap on simultaneously open static positions. Allows existing
            // positions to be managed (close/exit signals) but blocks new entries when full.
            if (_openPositions.Count >= _maxStaticPositions && !_openPositions.ContainsKey(symbol))
            {
                _logger.LogDebug("FundingHybrid[{Tier}] {Sym} new entry blocked: tier position cap {Open}/{Cap} full",
                    _tierName, symbol, _openPositions.Count, _maxStaticPositions);
                continue;
            }
            try
            {
                await ProcessSymbolAsync(symbol, basis * weight, marketData, runner, exchange, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FundingHybrid[{Tier}] tick failed for {Symbol}", _tierName, symbol);
            }
        }

        // Opportunistic 25-symbol scan — picks up funding spikes outside the static portfolio.
        if (_opportunisticEnabled && _opportunityScanner is not null)
        {
            await ProcessOpportunisticAsync(basis, marketData, runner, exchange, ct).ConfigureAwait(false);
        }

        SaveState();
    }

    private async Task ProcessOpportunisticAsync(
        decimal basis,
        IMarketDataService marketData,
        FundingAlphaBacktestRunner runner,
        IExchangeService? exchange,
        CancellationToken ct)
    {
        IReadOnlyList<FundingOpportunity> opps;
        try { opps = _opportunityScanner!.ScanOnce(DateTime.UtcNow); }
        catch (Exception ex) { _logger.LogWarning(ex, "Funding opportunistic scan failed."); return; }
        if (opps.Count == 0) return;

        // Threshold gate: opportunistic entry requires |funding| ≥ ThresholdMultiplier × static entry.
        var minOppFunding = _config.EntryThreshold * _opportunisticThresholdMultiplier;

        // Rank by |funding| descending; skip symbols already in static config or already open.
        var ranked = opps
            .Where(o => Math.Abs(o.FundingRate) >= minOppFunding)
            .Where(o => !_capitalPerSymbol.ContainsKey(StripDash(o.Symbol)))
            .Where(o => !_openPositions.ContainsKey(StripDash(o.Symbol)))
            .OrderByDescending(o => Math.Abs(o.FundingRate))
            .ToList();

        var openOpportunisticCount = _opportunisticSymbols.Count(s => _openPositions.ContainsKey(s));
        var slotsAvailable = _opportunisticMaxPositions - openOpportunisticCount;
        if (slotsAvailable <= 0)
        {
            if (ranked.Count > 0)
                _logger.LogDebug("Opportunistic scan: {N} candidates but slots full ({Open}/{Max})", ranked.Count, openOpportunisticCount, _opportunisticMaxPositions);
            return;
        }

        var sized = basis * _opportunisticSizeFraction;
        var taken = 0;
        foreach (var opp in ranked)
        {
            if (taken >= slotsAvailable || ct.IsCancellationRequested) break;
            var canonical = StripDash(opp.Symbol);
            try
            {
                _logger.LogWarning(
                    "OPPORTUNISTIC FUND {Symbol} funding={F:F5} ({Bps:F1}bp) conviction={C} dir={D} → routing through hybrid (capital=${Cap:N2})",
                    canonical, opp.FundingRate, opp.FundingBps, opp.Conviction, opp.Direction, sized);
                _opportunisticSymbols.Add(canonical);
                await ProcessSymbolAsync(canonical, sized, marketData, runner, exchange, ct).ConfigureAwait(false);
                // Only count as "taken" if a position actually opened (state machine may reject if signal not strong enough on the runner's bar).
                if (_openPositions.ContainsKey(canonical)) taken++;
                else _opportunisticSymbols.Remove(canonical);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Opportunistic entry failed for {Symbol}", canonical);
                _opportunisticSymbols.Remove(canonical);
            }
        }
    }

    private static string StripDash(string s) => s.Replace("-", "", StringComparison.Ordinal);

    private async Task ProcessSymbolAsync(
        string symbol,
        decimal capital,
        IMarketDataService marketData,
        FundingAlphaBacktestRunner runner,
        IExchangeService? exchange,
        CancellationToken ct)
    {
        var candles = await marketData.GetCandlesAsync(symbol, _candleLookback, "Crypto", _timeframe).ConfigureAwait(false);
        if (candles == null || candles.Count < 60)
        {
            _logger.LogDebug("FundingHybrid {Symbol} insufficient candles ({Count})", symbol, candles?.Count ?? 0);
            return;
        }

        // Regime-aware sizing: funding alpha (positioning fade) is strongest in Volatile (when
        // funding spikes happen during pump/dump), weakest in Range (calm market = small funding).
        var regime = MarketRegimeDetector.Detect(candles, lookback: 20, lagBars: 1, timeframe: _timeframe);
        var regimeMultiplier = regime.Regime switch
        {
            MarketRegime.Volatile => 1.0m,       // funding spikes happen here
            MarketRegime.Trending => 0.8m,       // some funding skew
            MarketRegime.Ranging => 0.5m,        // calm market = tiny funding edge
            _ => 0.5m                            // Unknown — half size
        };
        var adjustedCapital = capital * regimeMultiplier;
        if (regimeMultiplier < 1.0m)
        {
            _logger.LogDebug(
                "FundingHybrid {Symbol} regime={Regime} → sizing×{Mult:F2} (capital ${Orig:N0} → ${Adj:N0})",
                symbol, regime.Regime, regimeMultiplier, capital, adjustedCapital);
        }
        capital = adjustedCapital;

        // Pass our REAL position state so the runner doesn't gate live-signal entry on its
        // own backtest-internal `open`. Otherwise: backtest opens on latest bar → runner emits
        // HoldOpen → hybrid does nothing → entry silently swallowed. (Bug fix 2026-04-26.)
        var realIsFlat = !_openPositions.ContainsKey(symbol);
        var result = await runner.RunAsync(symbol, candles, _config, capital, ct, liveIsFlat: realIsFlat).ConfigureAwait(false);
        var sig = result.LiveSignal;

        // Daily loss reset (UTC midnight roll). Resets tier-wide PnL AND symbol-level
        // counters/blacklist so a previously-blacklisted symbol gets a fresh chance.
        if (DateTime.UtcNow.Date > _dailyResetUtc)
        {
            _dailyResetUtc = DateTime.UtcNow.Date;
            _dailyRealizedPnL = 0m;
            lock (_symbolLock)
            {
                _symbolDailyPnl.Clear();
                _symbolConsecutiveLosses.Clear();
                _symbolBlacklist.Clear();
            }
            if (_circuitBreakerTripped)
            {
                _circuitBreakerTripped = false;
                _ = _alerter?.SendAsync("circuit_breaker_reset", AlertSeverity.Info,
                    "Funding Circuit Breaker Reset",
                    "New trading day — circuit breaker auto-cleared. Hybrid resumes new entries.",
                    ct);
            }
        }

        // Block new entries if circuit breaker tripped — but still allow exits to flatten.
        if (_circuitBreakerTripped && sig.Kind == FundingLiveSignalKind.WouldOpenNew)
        {
            _logger.LogWarning("FundingHybrid[{Tier}] {Symbol} entry suppressed: TIER circuit breaker tripped (dailyPnL={DPnL:F2})", _tierName, symbol, _dailyRealizedPnL);
            return;
        }
        // Global cross-tier circuit breaker — second line of defence.
        if (_globalBreaker is not null && _globalBreaker.IsTripped && sig.Kind == FundingLiveSignalKind.WouldOpenNew)
        {
            _logger.LogWarning("FundingHybrid[{Tier}] {Symbol} entry suppressed: GLOBAL circuit breaker tripped via {TripTier} (cumPnL={Pnl:F2})",
                _tierName, symbol, _globalBreaker.TrippingTier, _globalBreaker.DailyRealizedPnl);
            return;
        }
        // Symbol-level breaker (Fix 3) — block new entries for symbols that have
        // already burned today. Existing positions can still close normally.
        bool isSymbolBlacklisted;
        int blacklistCl;
        decimal blacklistPnl;
        lock (_symbolLock)
        {
            isSymbolBlacklisted = _symbolBlacklist.Contains(symbol);
            blacklistCl = _symbolConsecutiveLosses.GetValueOrDefault(symbol);
            blacklistPnl = _symbolDailyPnl.GetValueOrDefault(symbol);
        }
        if (sig.Kind == FundingLiveSignalKind.WouldOpenNew && isSymbolBlacklisted)
        {
            _logger.LogWarning("FundingHybrid[{Tier}] {Symbol} entry suppressed: SYMBOL blacklisted today (consec losses={Cl} dailyPnL={Pnl:F2})",
                _tierName, symbol, blacklistCl, blacklistPnl);
            return;
        }

        // Macro event pause — only block NEW entries; let exits + holds proceed.
        var macroPause = MacroEventCalendarService.Current;

        switch (sig.Kind)
        {
            case FundingLiveSignalKind.WouldOpenNew when !_openPositions.ContainsKey(symbol):
                if (macroPause.IsPaused)
                {
                    _logger.LogInformation(
                        "FundingHybrid[{Tier}] {Sym} entry suppressed by macro pause ({Event}, T{Mins:+0;-0}m).",
                        _tierName, symbol, macroPause.EventName, macroPause.MinutesUntilEvent);
                    break;
                }
                await HandleOpenAsync(symbol, sig, exchange, ct).ConfigureAwait(false);
                break;

            case FundingLiveSignalKind.WouldClose when _openPositions.TryGetValue(symbol, out var existingForExit):
                await HandleCloseAsync(symbol, existingForExit, sig, exchange, ct).ConfigureAwait(false);
                break;

            case FundingLiveSignalKind.HoldOpen:
                // Trailing-stop check: track best price reached, arm once activation profit
                // hit, close on retracement. Worker-side dynamic exit — better fills than
                // OKX bracket SL (which stays as hard worst-case backstop). Disabled when
                // Funding:TrailingStopPct = 0 (default 0 = off, opt-in per tier via config).
                if (_openPositions.TryGetValue(symbol, out var openForTrail))
                {
                    var trailPct = ParseDecimal(Cfg("TrailingStopPct"), 0m);
                    var armPct   = ParseDecimal(Cfg("TrailingArmProfitPct"), 0.003m); // arm at 0.3% profit by default
                    if (trailPct > 0m)
                    {
                        var px = sig.ReferencePrice;
                        var maxFav = openForTrail.MaxFavorablePrice;
                        var armed  = openForTrail.TrailingArmed;
                        bool changed = false;
                        if (openForTrail.Side == TradeSide.Buy)
                        {
                            if (maxFav == 0m || px > maxFav) { maxFav = px; changed = true; }
                            if (!armed && maxFav >= openForTrail.EntryPrice * (1m + armPct)) { armed = true; changed = true; }
                            if (armed && px <= maxFav * (1m - trailPct))
                            {
                                _logger.LogWarning(
                                    "FundingHybrid[{Tier}] TRAILING-STOP triggered {Symbol} Buy: maxFav={Max:F4} cur={Cur:F4} retrace={R:P2} ≥ {Trail:P2}",
                                    _tierName, symbol, maxFav, px, (maxFav - px) / maxFav, trailPct);
                                var closeSig = new FundingLiveSignal(
                                    FundingLiveSignalKind.WouldClose, openForTrail.Side, openForTrail.Qty, px,
                                    openForTrail.EntryFunding, $"TrailingStop max={maxFav:F4} retrace={(maxFav-px)/maxFav:P2}");
                                await HandleCloseAsync(symbol, openForTrail, closeSig, exchange, ct).ConfigureAwait(false);
                                break;
                            }
                        }
                        else // Sell
                        {
                            if (maxFav == 0m || px < maxFav) { maxFav = px; changed = true; }
                            if (!armed && maxFav <= openForTrail.EntryPrice * (1m - armPct)) { armed = true; changed = true; }
                            if (armed && px >= maxFav * (1m + trailPct))
                            {
                                _logger.LogWarning(
                                    "FundingHybrid[{Tier}] TRAILING-STOP triggered {Symbol} Sell: minFav={Min:F4} cur={Cur:F4} retrace={R:P2} ≥ {Trail:P2}",
                                    _tierName, symbol, maxFav, px, (px - maxFav) / maxFav, trailPct);
                                var closeSig = new FundingLiveSignal(
                                    FundingLiveSignalKind.WouldClose, openForTrail.Side, openForTrail.Qty, px,
                                    openForTrail.EntryFunding, $"TrailingStop min={maxFav:F4} retrace={(px-maxFav)/maxFav:P2}");
                                await HandleCloseAsync(symbol, openForTrail, closeSig, exchange, ct).ConfigureAwait(false);
                                break;
                            }
                        }
                        if (changed)
                        {
                            _openPositions[symbol] = openForTrail with { MaxFavorablePrice = maxFav, TrailingArmed = armed };
                            try { SaveState(); } catch { /* non-critical */ }
                        }
                    }
                }
                _logger.LogDebug("FundingHybrid {Symbol} holding open position (entry={Entry:F4})", symbol, sig.ReferencePrice);
                break;

            case FundingLiveSignalKind.None:
                break;

            default:
                // WouldOpenNew but already open OR WouldClose but not in our state — log mismatch.
                _logger.LogWarning("FundingHybrid {Symbol} state mismatch: signal={Kind} hasOpen={HasOpen}", symbol, sig.Kind, _openPositions.ContainsKey(symbol));
                break;
        }
    }

    private async Task HandleOpenAsync(string symbol, FundingLiveSignal sig, IExchangeService? exchange, CancellationToken ct)
    {
        // Orphan-halt gate: if reconciliation has flagged this symbol as having an
        // unaccounted OKX position, refuse to open. Opening a second position over an
        // existing untracked one over-leverages the account and creates cascading
        // reconciliation noise — see OrphanHaltRegistry.cs for the full rationale.
        if (ThatsMyQuant.Application.Strategy.OrphanHaltRegistry.IsHalted(symbol))
        {
            _logger.LogWarning(
                "FundingHybrid[{Tier}] {Symbol} entry BLOCKED: symbol is orphan-halted (OKX has untracked position). Resolve on OKX or via adoption first.",
                _tierName, symbol);
            return;
        }

        // Settlement-bar mask: refuse entries within the configured minutes after an
        // OKX funding settlement boundary (00:00/08:00/16:00 UTC). Spreads widen and
        // volume craters around the boundary; entering there means systematically
        // worse fill prices than the backtest's mark-price assumption. Exits are NOT
        // gated — closing on a stop matters more than fee optimization.
        if (ThatsMyQuant.Application.Strategy.FundingAlphaBacktestRunner.IsSettlementBar(DateTime.UtcNow, _config))
        {
            _logger.LogInformation(
                "FundingHybrid[{Tier}] {Symbol} entry SKIPPED: within settlement-skip window ({Min} min after a UTC funding boundary).",
                _tierName, symbol, _config.SettlementSkipMinutes);
            return;
        }

        // Liquidation-distance safety: at the chosen leverage, the adverse price move
        // that triggers exchange liquidation is approximately (1/L − maintMarginRate).
        // A correctly-set stop-loss must fire BEFORE that liquidation point — and with
        // enough buffer to absorb slippage / settlement gaps. We require the liquidation
        // buffer to be at least 2× the stop-loss distance (industry-standard "2× SL"
        // rule), refusing the entry otherwise. At default Lev=1.0 this is trivially
        // satisfied; the gate matters when Tier 1 raises leverage.
        var effLev = Math.Max(1m, Math.Min(_config.Leverage, _config.MaxLeverage));
        // OKX swap-perp maintenance margin rate is ~0.5% across major USDT-margined
        // pairs (Tier 1 maint = 0.005). Read from config when explicitly tuned, else
        // use the conservative default. Higher tiers / smaller positions get lower
        // rates so 0.5% is a SAFE upper bound for the buffer calc.
        var maintMarginRate = decimal.TryParse(_configuration["Paper:Leverage:MaintenanceMarginRate"],
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture,
            out var m) && m > 0m ? m : 0.005m;
        var liqDistance = (1m / effLev) - maintMarginRate;
        const decimal MinLiqBufferMultiplier = 2.0m;
        if (_config.StopLossPct > 0m && liqDistance < _config.StopLossPct * MinLiqBufferMultiplier)
        {
            _logger.LogError(
                "FundingHybrid[{Tier}] {Symbol} entry BLOCKED: liquidation buffer {LiqDist:P2} < {Mult}× SL {SL:P2} at leverage {Lev}x. Reduce leverage or tighten SL.",
                _tierName, symbol, liqDistance, MinLiqBufferMultiplier, _config.StopLossPct, effLev);
            _ = _alerter?.SendAsync($"liq_buffer_{symbol}", AlertSeverity.Critical,
                $"Liquidation Buffer Insufficient [{_tierName}]",
                $"`{symbol}` entry refused: at {effLev}x leverage liquidation distance is {liqDistance:P2}, " +
                $"only {(liqDistance / _config.StopLossPct):F2}× the {_config.StopLossPct:P2} stop-loss (minimum 2.0×). " +
                $"Either reduce Funding:Leverage or tighten Funding:StopLossPct.",
                CancellationToken.None);
            return;
        }

        var clientOrderId = $"FH_{symbol}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        var notional = sig.Qty * sig.ReferencePrice;
        var basis = _portfolio.InitialBalance > 0m ? _portfolio.InitialBalance : 100_000m;

        // Notional cap — atomic reserve. If reservation fails, abort entry to prevent
        // cross-tier cash overrun. Released on close.
        if (_notionalGuard is not null && !_notionalGuard.TryReserve($"Funding:{_tierName}", symbol, notional, basis))
        {
            _logger.LogWarning(
                "FundingHybrid[{Tier}] {Symbol} entry SKIPPED: notional ${N:N2} would breach cross-tier cap.",
                _tierName, symbol, notional);
            return;
        }

        _logger.LogWarning(
            "[{Mode}] FUND_HYBRID OPEN {Symbol} {Side} qty={Qty:F6} px={Px:F4} funding={F:F5} | reason={R}",
            _mode, symbol, sig.Side, sig.Qty, sig.ReferencePrice, sig.CurrentFunding, sig.Reasoning);

        // Track the *actual* filled qty + price returned by OKX. The signal's requested
        // qty is the IDEAL size, but OKX swap orders get rounded to lotSz so the fill is
        // smaller (or 0). Persisting the requested qty causes false-positive RECONCILE
        // QTY-DRIFT alarms when the live position is checked against state. Always store
        // the OKX-confirmed qty (in base-asset units) so reconciliation matches.
        decimal storedQty = sig.Qty;
        decimal storedEntryPrice = sig.ReferencePrice;
        decimal storedEntryFee = 0m; // populated from Trade.TotalFeesPaid in live mode
        // Compute SL/TP trigger prices for OKX bracket attachment.
        // Long  → TP above entry, SL below entry.
        // Short → TP below entry, SL above entry.
        var slPx = sig.Side == TradeSide.Buy
            ? sig.ReferencePrice * (1m - _config.StopLossPct)
            : sig.ReferencePrice * (1m + _config.StopLossPct);
        var tpPx = sig.Side == TradeSide.Buy
            ? sig.ReferencePrice * (1m + _config.TakeProfitPct)
            : sig.ReferencePrice * (1m - _config.TakeProfitPct);

        if (_mode == HybridMode.Live && exchange is not null)
        {
            try
            {
                // 2026-04-28: maker-entry switch. OKX taker round-trip = 40bp (0.2%×2)
                // was eating ~60% of gross PnL. Routing entries as limit/post_only drops
                // the entry leg to ~8bp maker; OkxExecutionService handles the
                // post_only rejection path AND the LimitOrderTimeoutSeconds → market
                // fallback when the limit doesn't fill in time. Exit leg stays Market
                // (see HandleCloseAsync) — guaranteed-exit semantics outweigh fee
                // savings on stops, same prinicple as ExecuteTradeHandler.cs:404.
                // Disable per-tier with `Funding:{Tier}:MakerEntryEnabled=false`.
                var makerEntryEnabled = !bool.TryParse(Cfg("MakerEntryEnabled"), out var mee) || mee;
                var entryOrderType = makerEntryEnabled ? OrderType.Limit : OrderType.Market;
                var signal = new Signal
                {
                    Symbol = symbol,
                    Side = sig.Side,
                    Quantity = sig.Qty,
                    Price = sig.ReferencePrice,
                    OrderType = entryOrderType,
                    Confidence = 75,
                    StrategyTag = _strategyTag,
                    Reasoning = sig.Reasoning
                };
                signal.Metadata["ClientOrderId"] = clientOrderId;
                // Bracket SL/TP: when present, OkxExecutionService attaches algo orders
                // so positions are protected even if the worker crashes mid-trade.
                signal.Metadata["StopLossPx"]   = slPx.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                signal.Metadata["TakeProfitPx"] = tpPx.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                var trade = await exchange.ExecuteOrderAsync(signal).ConfigureAwait(false);

                // PHANTOM-OPEN GUARD (2026-04-30): if trade is null, has zero quantity,
                // or status is Pending/Rejected, the position was NOT actually opened on
                // OKX (post_only rejected + market fallback rejected, exchange-side reject,
                // poll outcome Unknown, etc). Without this guard the executor previously
                // logged "OPEN OK" with the IDEAL qty as a fallback default, persisted
                // state, and dispatched a Telegram OPENED notification — phantom open.
                // Operator sees Telegram says open, OKX says flat, reconciliation goes
                // into infinite RECONCILE GHOST loop, state file becomes corrupt.
                var actuallyFilled = trade is not null
                    && trade.Quantity > 0m
                    && trade.Status == TradeStatus.Executed;
                if (!actuallyFilled)
                {
                    _logger.LogError(
                        "FUND_HYBRID OPEN FAILED {Symbol} — venue did not fill (trade={HasTrade} qty={Qty} status={Status}). NO state recorded, NO Telegram open notice.",
                        symbol, trade is not null, trade?.Quantity ?? 0m, trade?.Status);
                    _ = _alerter?.SendAsync($"open_phantom_{symbol}", AlertSeverity.Warning,
                        $"Open Failed [{_tierName}] — `{symbol}`",
                        $"Order rejected or unfilled (likely post_only + market fallback both rejected, or fill-status unknown). Reconcile will sync; no phantom state recorded.",
                        CancellationToken.None);
                    _notionalGuard?.Release($"Funding:{_tierName}", symbol);
                    return;
                }

                storedQty = trade!.Quantity;
                if (trade.EntryPrice > 0m) storedEntryPrice = trade.EntryPrice;
                if (trade.TotalFeesPaid > 0m) storedEntryFee = trade.TotalFeesPaid;
                _logger.LogWarning("FUND_HYBRID OPEN OK {Symbol} TradeId={TradeId} fillPx={Px:F4} qty={Qty:F6} entryFee=${Fee:F4}", symbol, trade.Id, trade.EntryPrice, trade.Quantity, storedEntryFee);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FUND_HYBRID OPEN FAILED {Symbol} — keeping flat (no state recorded)", symbol);
                _notionalGuard?.Release($"Funding:{_tierName}", symbol);
                return;
            }
        }

        var nowUtc = DateTime.UtcNow;
        _openPositions[symbol] = new OpenPosition(
            Symbol: symbol,
            Side: sig.Side,
            Qty: storedQty,
            EntryPrice: storedEntryPrice,
            EntryFunding: sig.CurrentFunding,
            EntryTimeUtc: nowUtc,
            ClientOrderId: clientOrderId,
            ExecutionMode: _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN",
            EntryRealFee: storedEntryFee);

        // Mirror open into PaperPortfolio so the dashboard's paper_portfolio.json
        // snapshot reflects live exposure. Matched on close by Symbol+EntryTime.
        var openMirror = new PaperPosition
        {
            Symbol = symbol,
            Side = sig.Side,
            EntryPrice = storedEntryPrice,
            CurrentPrice = storedEntryPrice,
            Quantity = storedQty,
            OpenedQuantity = storedQty,
            RemainingQuantity = storedQty,
            StopLoss = slPx,
            TakeProfit = tpPx,
            EntryTime = nowUtc,
            StrategyTag = _strategyTag,
            RegimeTag = $"funding={sig.CurrentFunding:F5}",
            ExecutionMode = _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN",
            PromotionStage = "Paper",
            IsLeveragedPaperPosition = true,
            Reasoning = sig.Reasoning ?? string.Empty,
            Confidence = 75,
        };
        lock (_portfolio) { _portfolio.OpenPositions.Add(openMirror); }
        _persistence?.RequestWrite(_portfolio);

        // [TCA persistence] mirror open into SQLite Trade table.
        await PersistFundingOpenAsync(symbol, sig, storedQty, storedEntryPrice, clientOrderId, nowUtc).ConfigureAwait(false);

        // Publish PositionOpenedNotification — TelegramPositionPushHandler subscribes to this
        // and sends the "🟢 OPENED" push. Without this publish, the operator sees no Telegram
        // entry/exit feed (only structural alerts via IRealtimeAlerter). Fire-and-forget so a
        // slow Telegram round-trip doesn't block the executor's tick.
        try
        {
            var note = new PositionOpenedNotification(
                Symbol: symbol,
                StrategyTag: _strategyTag,
                RegimeTag: $"funding={sig.CurrentFunding:F5}",
                ExitPolicyTag: $"TP{_config.TakeProfitPct:P1}/SL{_config.StopLossPct:P1}/Hold{_config.MaxHoldingBars}h",
                ExecutionMode: _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN",
                Side: sig.Side,
                EntryPrice: storedEntryPrice,
                Quantity: storedQty,
                StopLoss: slPx,
                TakeProfit: tpPx,
                EdgeScore: 0m,
                Confidence: 75m,
                Reasoning: sig.Reasoning ?? string.Empty,
                EntryTimeUtc: nowUtc,
                CorrelationId: clientOrderId);
            _ = PublishNotificationAsync(note);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FundingHybrid: failed to publish PositionOpenedNotification for {Symbol}", symbol);
        }
    }

    /// <summary>[TCA persistence] Persist a Trade row on funding-open.</summary>
    private async Task PersistFundingOpenAsync(string symbol, FundingLiveSignal sig, decimal qty, decimal entryPrice, string clientOrderId, DateTime nowUtc)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetService<ITradeRepository>();
            if (repo is null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var trade = new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = Guid.NewGuid(),
                Symbol = symbol,
                Side = sig.Side,
                EntryPrice = entryPrice,
                Quantity = qty,
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Active,
                StrategyTag = _strategyTag,
                RegimeTag = $"funding={sig.CurrentFunding:F5}",
                ExitPolicyTag = $"{{clordid:\"{clientOrderId}\",entryFunding:{sig.CurrentFunding.ToString("F6", inv)}}}",
                EntryTime = nowUtc,
                ExecutionSource = _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN",
                PromotionStage = "Paper",
                SignalPrice = entryPrice,
            };
            await repo.AddAsync(trade).ConfigureAwait(false);
            _tradeIdByLifecycle[$"{symbol}|{nowUtc.Ticks}"] = trade.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FundingHybrid[{Tier}]: ITradeRepository AddAsync failed (open) for {Symbol}", _tierName, symbol);
        }
    }

    /// <summary>[TCA persistence] Update Trade row on funding-close.</summary>
    private async Task PersistFundingCloseAsync(string symbol, OpenPosition existing, FundingLiveSignal sig, decimal grossPnl, decimal cost, decimal netPnl)
    {
        try
        {
            var key = $"{symbol}|{existing.EntryTimeUtc.Ticks}";
            if (!_tradeIdByLifecycle.TryGetValue(key, out var tradeId))
            {
                _logger.LogWarning("FundingHybrid: no Trade.Id for {Symbol} entryTime={ET:o} — close NOT persisted (worker restart mid-position).", symbol, existing.EntryTimeUtc);
                return;
            }
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetService<ITradeRepository>();
            if (repo is null) return;
            var trade = await repo.GetByIdAsync(tradeId).ConfigureAwait(false);
            if (trade is null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            trade.ExitPrice = sig.ReferencePrice;
            trade.ExitTime = DateTime.UtcNow;
            trade.LifecycleStatus = TradeLifecycleStatus.Closed;
            trade.Status = TradeStatus.Executed;
            trade.TotalFeesPaid = cost;
            trade.ExitReason = sig.Reasoning ?? string.Empty;
            trade.ExitPolicyTag = trade.ExitPolicyTag.TrimEnd('}') +
                $",exitFunding:{sig.CurrentFunding.ToString("F6", inv)}," +
                $"gross:{grossPnl.ToString("F4", inv)},cost:{cost.ToString("F4", inv)},net:{netPnl.ToString("F4", inv)}}}";
            await repo.UpdateAsync(trade).ConfigureAwait(false);
            _tradeIdByLifecycle.Remove(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FundingHybrid: ITradeRepository UpdateAsync failed (close) for {Symbol}", symbol);
        }
    }

    private async Task HandleCloseAsync(string symbol, OpenPosition existing, FundingLiveSignal sig, IExchangeService? exchange, CancellationToken ct)
    {
        var oppositeSide = existing.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        var clientOrderId = $"FH_{symbol}_CLOSE_{DateTime.UtcNow:yyyyMMddHHmmss}";

        // Initial estimate using mark price + assumed cost — will be REPLACED below with
        // measured values from the venue Trade response when running live. The estimate
        // remains the canonical answer only in dry-run / pure-simulation mode.
        var actualExitPrice = sig.ReferencePrice;
        decimal? realRoundTripFee = null;

        if (_mode == HybridMode.Live && exchange is not null)
        {
            try
            {
                var signal = new Signal
                {
                    Symbol = symbol,
                    Side = oppositeSide,
                    Quantity = existing.Qty,
                    Price = sig.ReferencePrice,
                    OrderType = OrderType.Market,
                    Confidence = 75,
                    StrategyTag = _strategyTag,
                    Reasoning = $"Close: {sig.Reasoning}"
                };
                signal.Metadata["ClientOrderId"] = clientOrderId;
                signal.Metadata["ReduceOnly"] = "true";
                var trade = await exchange.ExecuteOrderAsync(signal).ConfigureAwait(false);
                if (trade is not null)
                {
                    if (trade.ExitPrice > 0m) actualExitPrice = trade.ExitPrice;
                    var exitFee = trade.TotalFeesPaid;
                    if (existing.EntryRealFee > 0m && exitFee > 0m)
                    {
                        // Both legs measured — canonical real round-trip cost.
                        realRoundTripFee = existing.EntryRealFee + exitFee;
                    }
                    else if (exitFee > 0m)
                    {
                        // Exit only — approximate by doubling (legs symmetric on OKX VIP-0).
                        realRoundTripFee = exitFee * 2m;
                    }
                }
                _logger.LogWarning(
                    "FUND_HYBRID CLOSE OK {Symbol} TradeId={TradeId} fillPx={Px:F4} entryFee=${EF:F4} exitFee=${XF:F4} realRT=${RT:F4}",
                    symbol, trade?.Id, actualExitPrice, existing.EntryRealFee, trade?.TotalFeesPaid ?? 0m, realRoundTripFee ?? 0m);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FUND_HYBRID CLOSE FAILED {Symbol} — position remains tracked locally; manual reconciliation needed", symbol);
                return;
            }
        }

        // Now compute PnL with the actual fill price + real fee where available.
        var pnl = existing.Side == TradeSide.Buy
            ? (actualExitPrice - existing.EntryPrice) * existing.Qty
            : (existing.EntryPrice - actualExitPrice) * existing.Qty;
        var costApplied = realRoundTripFee
            ?? (actualExitPrice * existing.Qty * _config.RoundTripCostRate);
        var netPnl = pnl - costApplied;

        _logger.LogWarning(
            "[{Mode}] FUND_HYBRID CLOSE {Symbol} {Side}->{OppSide} qty={Qty:F6} entry={Entry:F4} exit={Exit:F4} gross=${G:F4} cost=${C:F4} net=${N:F4} costSrc={Src} reason={R}",
            _mode, symbol, existing.Side, oppositeSide, existing.Qty, existing.EntryPrice, actualExitPrice,
            pnl, costApplied, netPnl, realRoundTripFee.HasValue ? "MEASURED" : "ESTIMATED", sig.Reasoning);

        // Mirror to PaperPortfolio (canonical record of the trade lifecycle).
        var position = new PaperPosition
        {
            Symbol = symbol,
            Side = existing.Side,
            EntryPrice = existing.EntryPrice,
            CurrentPrice = actualExitPrice,
            Quantity = existing.Qty,
            EntryTime = existing.EntryTimeUtc,
            ExitTime = DateTime.UtcNow,
            RealizedPnL = netPnl,
            ExitReason = sig.Reasoning,
            Reasoning = $"Hybrid close: entryFunding={existing.EntryFunding:F5} exitFunding={sig.CurrentFunding:F5}",
            StrategyTag = "FundingAlpha-Hybrid",
            RegimeTag = "FundingMeanReversion",
            ExecutionMode = _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN",
            PromotionStage = "Paper",
            NetFunding = existing.EntryFunding,
            RealFeePaid = realRoundTripFee
        };
        lock (_portfolio)
        {
            _portfolio.OpenPositions.RemoveAll(p => p.Symbol == symbol && p.EntryTime == existing.EntryTimeUtc);
            _portfolio.ClosedPositions.Add(position);
            _portfolio.Cash += netPnl;
            _portfolio.DailyPnL += netPnl;
            _portfolio.AppendEquity(_portfolio.TotalEquity);
        }
        _persistence?.RequestWrite(_portfolio);

        // Daily PnL accounting + circuit breaker check.
        _dailyRealizedPnL += netPnl;
        // Symbol-level breaker (Fix 3): track per-symbol loss streaks + daily PnL.
        RecordSymbolPnl(symbol, netPnl);
        var basis = _portfolio.InitialBalance > 0m ? _portfolio.InitialBalance : 100_000m;
        var lossPct = _dailyRealizedPnL < 0m ? -_dailyRealizedPnL / basis : 0m;
        if (!_circuitBreakerTripped && lossPct >= _circuitBreakerLossPctOfBasis)
        {
            _circuitBreakerTripped = true;
            _logger.LogCritical(
                "CIRCUIT BREAKER TRIPPED (Funding): daily loss ${Loss:F2} = {LossPct:P2} of basis ${Basis:F2} (threshold {Threshold:P2}). New entries suppressed until UTC midnight.",
                _dailyRealizedPnL, lossPct, basis, _circuitBreakerLossPctOfBasis);
            await (_alerter?.SendAsync("circuit_breaker_funding", AlertSeverity.Critical,
                "Funding Circuit Breaker TRIPPED",
                $"Daily realized PnL: ${_dailyRealizedPnL:F2} = {lossPct:P2} of basis ${basis:F2}.\nThreshold {_circuitBreakerLossPctOfBasis:P2} breached.\nNew entries SUPPRESSED until UTC midnight. Existing positions still managed.",
                CancellationToken.None) ?? Task.CompletedTask).ConfigureAwait(false);
        }

        // Global cross-tier circuit breaker — Long/Short cumulative aggregation.
        if (_globalBreaker is not null)
        {
            var globalTrippedNow = _globalBreaker.RecordRealizedPnl($"Funding:{_tierName}", netPnl, basis);
            if (globalTrippedNow)
            {
                await (_alerter?.SendAsync("global_circuit_breaker", AlertSeverity.Critical,
                    "GLOBAL Circuit Breaker TRIPPED",
                    $"Cross-tier daily PnL: ${_globalBreaker.DailyRealizedPnl:F2} = {(-_globalBreaker.DailyRealizedPnl/basis):P2} of basis ${basis:F2}.\nThreshold {_globalBreaker.Threshold:P2} breached via {_tierName}.\nALL TIER ENTRIES SUPPRESSED until UTC midnight.",
                    CancellationToken.None) ?? Task.CompletedTask).ConfigureAwait(false);
            }
        }

        _openPositions.Remove(symbol);
        _opportunisticSymbols.Remove(symbol);
        _notionalGuard?.Release($"Funding:{_tierName}", symbol);

        // [TCA persistence] update Trade row with exit + PnL. Pass the actually-applied
        // cost (real round-trip when measured, else the estimate) so the SQLite Trade row
        // matches the PaperPortfolio mirror.
        await PersistFundingCloseAsync(symbol, existing, sig, pnl, costApplied, netPnl).ConfigureAwait(false);

        // Publish PositionClosedNotification — TelegramPositionPushHandler renders the
        // "✅/❌ CLOSED" push and AiTradeReviewHandler queues the post-mortem. Fire-and-forget.
        try
        {
            var pnlPct = existing.EntryPrice > 0m ? (netPnl / (existing.EntryPrice * existing.Qty)) * 100m : 0m;
            var note = new PositionClosedNotification(
                Symbol: symbol,
                StrategyTag: _strategyTag,
                RegimeTag: $"funding={existing.EntryFunding:F5}->{sig.CurrentFunding:F5}",
                ExitPolicyTag: $"TP{_config.TakeProfitPct:P1}/SL{_config.StopLossPct:P1}",
                ExitReason: sig.Reasoning ?? "Unknown",
                ExecutionMode: _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN",
                Side: existing.Side,
                EntryPrice: existing.EntryPrice,
                ExitPrice: sig.ReferencePrice,
                Quantity: existing.Qty,
                NetPnl: netPnl,
                NetPnlPercent: pnlPct,
                EntryTimeUtc: existing.EntryTimeUtc,
                ExitTimeUtc: DateTime.UtcNow,
                CorrelationId: existing.ClientOrderId);
            _ = PublishNotificationAsync(note);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FundingHybrid: failed to publish PositionClosedNotification for {Symbol}", symbol);
        }
    }

    /// <summary>
    /// Resolves IPublisher from a fresh DI scope and publishes the notification.
    /// Runs detached (await with .ContinueWith for error logging) so the executor
    /// tick is never blocked by Telegram round-trip latency or MediatR handler stalls.
    /// </summary>
    private async Task PublishNotificationAsync(INotification notification)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var publisher = scope.ServiceProvider.GetService<IPublisher>();
            if (publisher is null) return;
            await publisher.Publish(notification, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FundingHybrid: PublishNotificationAsync threw for {Type}", notification.GetType().Name);
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var json = File.ReadAllText(_statePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, OpenPosition>>(json);
            if (loaded is not null)
            {
                foreach (var kv in loaded) _openPositions[kv.Key] = kv.Value;
                _logger.LogInformation("FundingHybridExecutor: loaded {Count} open positions from state file.", _openPositions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FundingHybridExecutor: state load failed (continuing with empty state).");
        }
    }

    private void SaveState()
    {
        try
        {
            var json = JsonSerializer.Serialize(_openPositions, new JsonSerializerOptions { WriteIndented = true });
            // Atomic write: temp file + Move with overwrite. Prevents partial-write corruption
            // when the worker crashes mid-write. NTFS guarantees Move-with-overwrite atomicity.
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FundingHybridExecutor: state save failed.");
        }
    }

    private static decimal ParseDecimal(string? raw, decimal fallback)
        => decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public sealed record OpenPosition(
        string Symbol,
        TradeSide Side,
        decimal Qty,
        decimal EntryPrice,
        decimal EntryFunding,
        DateTime EntryTimeUtc,
        string ClientOrderId,
        // BUG-FIX 2026-04-26: track execution mode per-position so reconciliation can
        // skip dry-run positions (no real OKX order placed → comparing to OKX is noise).
        // Default empty for back-compat; old state files without this field deserialize fine.
        string ExecutionMode = "",
        // 2026-04-27: trailing-stop tracking. MaxFavorablePrice = best price reached
        // since entry (highest for longs, lowest for shorts). Each tick we update it
        // and arm the trailing stop once a configurable activation profit is hit. Once
        // armed, if price retraces by Funding:TrailingStopPct from MaxFavorable, the
        // executor closes the position. Worker-side trailing — OKX bracket SL stays
        // as the worst-case backstop in case the worker is offline.
        decimal MaxFavorablePrice = 0m,
        bool TrailingArmed = false,
        // 2026-04-30: capture entry-leg real fee from the Trade.TotalFeesPaid returned
        // by IExchangeService.ExecuteOrderAsync at open time. Combined with the exit-leg
        // fee at close time, this gives the true round-trip fee per position — populated
        // into PaperPosition.RealFeePaid so TCA / ShadowAudit / promotion-gate all see
        // measured cost instead of the assumed RoundTripCostRate. Default 0 = unknown
        // (dry-run mode or back-compat state files).
        decimal EntryRealFee = 0m);

    private enum HybridMode { Off, DryRun, Live }
}
