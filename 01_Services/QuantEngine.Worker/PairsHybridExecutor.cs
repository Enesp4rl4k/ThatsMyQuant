using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Hybrid pairs executor — bridges PairsBacktestRunner with REAL OKX demo execution. For
/// each pair tick: extracts the runner's "live signal" on the latest bar, maintains a
/// per-pair position state machine, and (in live mode) places TWO real OKX market orders
/// per entry/exit (one for each leg, sized by hedge ratio β).
///
/// Three modes via <c>Strategy:PairsTrading:HybridMode</c>:
///   - <c>off</c>     : disabled (legacy <see cref="PairsTradingPaperHostedService"/> handles).
///   - <c>dry-run</c> : evaluates + logs WHAT WOULD HAPPEN. No real orders. Default + safe.
///   - <c>live</c>    : same + calls ExecuteOrderAsync on each leg.
///
/// State persisted to JSON so positions survive restarts. Critical: pair positions involve
/// TWO legs — partial failure (leg A places, leg B rejects) leaves us imbalanced. The
/// executor logs an alert and skips state mutation in that case so reconciliation catches it.
/// </summary>
public sealed class PairsHybridExecutor : BackgroundService
{
    private readonly ILogger<PairsHybridExecutor> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly PaperPortfolio _portfolio;
    private readonly string _tierName;          // "Short", "Long", or "" (legacy)
    private readonly string _strategyTag;       // "PairsTrading-Hybrid-Short", etc.
    private readonly TimeSpan _interval;
    private readonly string _timeframe;
    private readonly int _candleLookback;
    private readonly HybridMode _mode;
    private readonly string _statePath;
    private readonly List<PairConfig> _pairs;
    private readonly Dictionary<string, OpenPairPosition> _openPositions = new(StringComparer.OrdinalIgnoreCase);
    // [TCA persistence] pair.Key+EntryTime → SQLite Trade.Id, so close can update the same row.
    // In-memory only (paper restarts lose mapping; close handler then logs warning instead of writing).
    private readonly Dictionary<string, Guid> _tradeIdByPairLifecycle = new(StringComparer.OrdinalIgnoreCase);
    private readonly IRealtimeAlerter? _alerter;
    private decimal _dailyRealizedPnL = 0m;
    private DateTime _dailyResetUtc = DateTime.UtcNow.Date;
    private bool _circuitBreakerTripped = false;
    private readonly decimal _circuitBreakerLossPctOfBasis;
    private readonly int _maxConcurrentPairs;

    private readonly GlobalCircuitBreaker? _globalBreaker;
    private readonly NotionalExposureGuard? _notionalGuard;
    private readonly PaperPortfolioPersistenceService? _persistence;

    public PairsHybridExecutor(
        ILogger<PairsHybridExecutor> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null,
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
        _tierName = tierName ?? string.Empty;
        _strategyTag = string.IsNullOrEmpty(_tierName)
            ? "PairsTrading-Hybrid"
            : $"PairsTrading-Hybrid-{_tierName}";

        _circuitBreakerLossPctOfBasis = decimal.TryParse(configuration["Hybrid:CircuitBreakerDailyLossPct"],
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cb) && cb > 0m
            ? cb : 0.03m;

        var modeRaw = Cfg("HybridMode") ?? "off";
        _mode = modeRaw.Trim().ToLowerInvariant() switch
        {
            "live" => HybridMode.Live,
            "dry-run" or "dryrun" => HybridMode.DryRun,
            _ => HybridMode.Off
        };

        _timeframe = Cfg("Timeframe") ?? "4h";
        _candleLookback = int.TryParse(Cfg("CandleLookback"), out var cl) && cl > 0 ? cl : 300;
        var intervalSeconds = int.TryParse(Cfg("IntervalSeconds"), out var iv) && iv > 0 ? iv : 300;
        _interval = TimeSpan.FromSeconds(intervalSeconds);
        _maxConcurrentPairs = int.TryParse(Cfg("MaxConcurrentPairs"), out var mcp) && mcp > 0 ? mcp : int.MaxValue;

        _pairs = new List<PairConfig>();
        var csv = Cfg("AllowedPairsCsv");
        if (!string.IsNullOrWhiteSpace(csv))
        {
            foreach (var entry in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    decimal weight = 0.10m;
                    if (parts.Length >= 3 && decimal.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var raw))
                    {
                        weight = raw > 1.0m ? raw / 100_000m : raw;
                    }
                    _pairs.Add(new PairConfig(parts[0], parts[1], weight));
                }
            }
        }

        var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant");
        Directory.CreateDirectory(stateDir);
        var stateFileName = string.IsNullOrEmpty(_tierName)
            ? "pairs_hybrid_state.json"
            : $"pairs_{_tierName.ToLowerInvariant()}_state.json";
        _statePath = Path.Combine(stateDir, stateFileName);
        if (!File.Exists(_statePath) && _tierName.Equals("Long", StringComparison.OrdinalIgnoreCase))
        {
            var legacyPath = Path.Combine(stateDir, "pairs_hybrid_state.json");
            if (File.Exists(legacyPath))
            {
                _logger.LogInformation("PairsHybridExecutor[{Tier}]: migrating state from legacy path {Legacy} → {New}", _tierName, legacyPath, _statePath);
                File.Copy(legacyPath, _statePath);
            }
        }
        LoadState();
    }

    /// <summary>Tier-aware config lookup. Reads <c>Strategy:PairsTrading:{Tier}:{key}</c> first,
    /// then <c>Strategy:PairsTrading:{key}</c> for backward compat.</summary>
    private string? Cfg(string key)
    {
        if (!string.IsNullOrEmpty(_tierName))
        {
            var tiered = _configuration[$"Strategy:PairsTrading:{_tierName}:{key}"];
            if (!string.IsNullOrWhiteSpace(tiered)) return tiered;
        }
        return _configuration[$"Strategy:PairsTrading:{key}"];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_mode == HybridMode.Off || _pairs.Count == 0)
        {
            _logger.LogInformation("PairsHybridExecutor disabled (Mode={Mode} PairCount={N}).", _mode, _pairs.Count);
            return;
        }

        _logger.LogWarning(
            "PairsHybridExecutor[{Tier}] starting in {Mode} mode. Pairs={Pairs} Interval={Interval}s Timeframe={Tf} MaxConcurrent={MaxC} (open={Open})",
            string.IsNullOrEmpty(_tierName) ? "single" : _tierName,
            _mode, string.Join(",", _pairs.Select(p => p.Key)), _interval.TotalSeconds, _timeframe,
            _maxConcurrentPairs == int.MaxValue ? "∞" : _maxConcurrentPairs.ToString(),
            _openPositions.Count);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try
                {
                    await ProcessAllPairsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PairsHybridExecutor tick failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAllPairsAsync(CancellationToken ct)
    {
        var basis = _portfolio.InitialBalance;
        if (basis <= 0m)
        {
            _logger.LogWarning("PairsHybrid skipped: PaperPortfolio.InitialBalance=0 (waiting for OKX balance sync).");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var exchange = scope.ServiceProvider.GetService<IExchangeService>();

        if (_mode == HybridMode.Live && exchange is null)
        {
            _logger.LogError("PairsHybrid LIVE mode requires IExchangeService — none registered. Tick aborted.");
            return;
        }

        foreach (var pair in _pairs)
        {
            if (ct.IsCancellationRequested) return;
            // Per-tier hard cap on concurrent pair positions. Allows existing pairs to be
            // managed (close/exit) but blocks new entries when full.
            if (_openPositions.Count >= _maxConcurrentPairs && !_openPositions.ContainsKey(pair.Key))
            {
                _logger.LogDebug("PairsHybrid[{Tier}] {Pair} new entry blocked: tier cap {Open}/{Cap} full",
                    _tierName, pair.Key, _openPositions.Count, _maxConcurrentPairs);
                continue;
            }
            try
            {
                await ProcessPairAsync(pair, basis * pair.WeightFraction, marketData, loggerFactory, exchange, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PairsHybrid[{Tier}] tick failed for {Pair}", _tierName, pair.Key);
            }
        }

        SaveState();
    }

    private async Task ProcessPairAsync(
        PairConfig pair,
        decimal capital,
        IMarketDataService marketData,
        ILoggerFactory loggerFactory,
        IExchangeService? exchange,
        CancellationToken ct)
    {
        var candlesY = await marketData.GetCandlesAsync(pair.SymbolY, _candleLookback, "Crypto", _timeframe).ConfigureAwait(false);
        var candlesX = await marketData.GetCandlesAsync(pair.SymbolX, _candleLookback, "Crypto", _timeframe).ConfigureAwait(false);
        if (candlesY == null || candlesX == null || candlesY.Count < 100 || candlesX.Count < 100)
        {
            _logger.LogDebug("PairsHybrid {Pair} insufficient candles", pair.Key);
            return;
        }

        // Regime-aware sizing: pairs (mean-reversion) is strongest in Range, weakest in Volatile.
        // The multiplier scales the per-pair capital allocation; cointegration math itself is
        // unchanged, only the SIZE of the resulting position. Existing position is unaffected
        // (we only modify capital for NEW entries).
        var regimeY = MarketRegimeDetector.Detect(candlesY, lookback: 20, lagBars: 1, timeframe: _timeframe);
        var regimeMultiplier = regimeY.Regime switch
        {
            MarketRegime.Ranging => 1.0m,        // mean-reversion ideal
            MarketRegime.Trending => 0.7m,       // weaker, β can drift
            MarketRegime.Volatile => 0.4m,       // tail risk + cointegration unstable
            _ => 0.5m                            // Unknown — half size for safety
        };
        var adjustedCapital = capital * regimeMultiplier;
        if (regimeMultiplier < 1.0m)
        {
            _logger.LogDebug(
                "PairsHybrid {Pair} regime={Regime} → sizing×{Mult:F2} (capital ${Orig:N0} → ${Adj:N0})",
                pair.Key, regimeY.Regime, regimeMultiplier, capital, adjustedCapital);
        }
        capital = adjustedCapital;

        var pairOptions = new PairsTradingOptions
        {
            SymbolA = pair.SymbolY,
            SymbolB = pair.SymbolX,
            CointegrationLookbackBars = 180,
            ZScoreWindow = 30,
            MinCorrelation = 0.30,
            MaxHedgeRatio = 15.00m,
            MinHedgeRatio = 0.10m,
            // Kalman-filtered β adapts the hedge ratio every bar instead of waiting for the
            // periodic OLS rebalance — better for crypto pairs where regime shifts fast.
            // Memory: 'pairs_phase2_2026_04_24' Kalman implementation already validated.
            UseKalmanHedgeRatio = true,
            KalmanProcessVariance = 1e-4,
            KalmanInitialVariance = 1.0
        };
        var runner = new PairsBacktestRunner(loggerFactory.CreateLogger<PairsBacktestRunner>(), Options.Create(pairOptions));
        // Pass our REAL pair-position state so runner doesn't gate entry on backtest-internal state.
        var realIsFlat = !_openPositions.ContainsKey(pair.Key);
        runner.Run(candlesY, candlesX, capital, liveIsFlat: realIsFlat);

        var sig = runner.LastLiveSignal ?? PairsLiveSignal.None;
        var hasOpen = _openPositions.ContainsKey(pair.Key);

        // Daily PnL reset at UTC midnight.
        if (DateTime.UtcNow.Date > _dailyResetUtc)
        {
            _dailyResetUtc = DateTime.UtcNow.Date;
            _dailyRealizedPnL = 0m;
            if (_circuitBreakerTripped)
            {
                _circuitBreakerTripped = false;
                _ = _alerter?.SendAsync("circuit_breaker_pairs_reset", AlertSeverity.Info,
                    "Pairs Circuit Breaker Reset",
                    "New trading day — circuit breaker auto-cleared. Hybrid resumes new entries.",
                    ct);
            }
        }
        if (_circuitBreakerTripped && sig.Kind == PairsLiveSignalKind.WouldOpenNew)
        {
            _logger.LogWarning("PairsHybrid[{Tier}] {Pair} entry suppressed: TIER circuit breaker tripped (dailyPnL={DPnL:F2})", _tierName, pair.Key, _dailyRealizedPnL);
            return;
        }
        // Global cross-tier circuit breaker.
        if (_globalBreaker is not null && _globalBreaker.IsTripped && sig.Kind == PairsLiveSignalKind.WouldOpenNew)
        {
            _logger.LogWarning("PairsHybrid[{Tier}] {Pair} entry suppressed: GLOBAL circuit breaker tripped via {TripTier} (cumPnL={Pnl:F2})",
                _tierName, pair.Key, _globalBreaker.TrippingTier, _globalBreaker.DailyRealizedPnl);
            return;
        }

        // Macro event pause — block NEW entries only.
        var macroPause = MacroEventCalendarService.Current;

        switch (sig.Kind)
        {
            case PairsLiveSignalKind.WouldOpenNew when !hasOpen:
                if (macroPause.IsPaused)
                {
                    _logger.LogInformation(
                        "PairsHybrid[{Tier}] {Pair} entry suppressed by macro pause ({Event}, T{Mins:+0;-0}m).",
                        _tierName, pair.Key, macroPause.EventName, macroPause.MinutesUntilEvent);
                    break;
                }
                await HandleOpenAsync(pair, sig, exchange, ct).ConfigureAwait(false);
                break;

            case PairsLiveSignalKind.WouldClose when hasOpen:
                await HandleCloseAsync(pair, _openPositions[pair.Key], sig, exchange, ct).ConfigureAwait(false);
                break;

            case PairsLiveSignalKind.HoldOpen:
                _logger.LogDebug("PairsHybrid {Pair} HOLDING (entry Z={Z:F2})", pair.Key, sig.EntryZScore);
                break;

            case PairsLiveSignalKind.None:
                break;

            default:
                _logger.LogWarning("PairsHybrid {Pair} STATE MISMATCH signal={Kind} hasOpen={HasOpen}", pair.Key, sig.Kind, hasOpen);
                break;
        }
    }

    private async Task HandleOpenAsync(PairConfig pair, PairsLiveSignal sig, IExchangeService? exchange, CancellationToken ct)
    {
        // Orphan-halt gate: pairs trade is two legs — if EITHER leg's symbol has an
        // unaccounted OKX position, refuse to open the whole pair. A half-hedged spread
        // is worse than no trade.
        if (ThatsMyQuant.Application.Strategy.OrphanHaltRegistry.IsHalted(pair.SymbolY)
            || ThatsMyQuant.Application.Strategy.OrphanHaltRegistry.IsHalted(pair.SymbolX))
        {
            _logger.LogWarning(
                "PairsHybrid[{Tier}] {Pair} entry BLOCKED: at least one leg orphan-halted (Y={Y} X={X}).",
                _tierName, pair.Key, pair.SymbolY, pair.SymbolX);
            return;
        }

        // LongSpread = Long Y, Short X.    ShortSpread = Short Y, Long X.
        var sideY = sig.Side == PairsLiveSide.LongSpread ? TradeSide.Buy : TradeSide.Sell;
        var sideX = sig.Side == PairsLiveSide.LongSpread ? TradeSide.Sell : TradeSide.Buy;
        var clientOrderId = $"PH_{pair.Key}_{DateTime.UtcNow:yyyyMMddHHmmss}";

        // Notional cap reservation (sum both legs). Aborts if it would breach cross-tier cap.
        var notionalY = sig.QtyA * sig.PriceA;
        var notionalX = sig.QtyB * sig.PriceB;
        var totalNotional = notionalY + notionalX;
        var basis = _portfolio.InitialBalance > 0m ? _portfolio.InitialBalance : 100_000m;
        if (_notionalGuard is not null && !_notionalGuard.TryReserve($"Pairs:{_tierName}", pair.Key, totalNotional, basis))
        {
            _logger.LogWarning(
                "PairsHybrid[{Tier}] {Pair} entry SKIPPED: total notional ${N:N2} would breach cross-tier cap.",
                _tierName, pair.Key, totalNotional);
            return;
        }

        _logger.LogWarning(
            "[{Mode}] PAIR_HYBRID OPEN {Pair} {SpreadSide} | LegY={SymY} {SideY} qty={QtyA:F6} px={PxA:F4} | LegX={SymX} {SideX} qty={QtyB:F6} px={PxB:F4} | β={Beta:F3} entryZ={Z:F2}",
            _mode, pair.Key, sig.Side, pair.SymbolY, sideY, sig.QtyA, sig.PriceA, pair.SymbolX, sideX, sig.QtyB, sig.PriceB, sig.HedgeRatio, sig.EntryZScore);

        Trade? legYTrade = null;
        Trade? legXTrade = null;
        if (_mode == HybridMode.Live && exchange is not null)
        {
            try { legYTrade = await PlaceLeg(exchange, pair.SymbolY, sideY, sig.QtyA, sig.PriceA, $"{clientOrderId}_Y").ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "PAIR_HYBRID OPEN {Pair} leg-Y FAILED — skipping leg-X to avoid imbalance", pair.Key); _notionalGuard?.Release($"Pairs:{_tierName}", pair.Key); return; }

            try { legXTrade = await PlaceLeg(exchange, pair.SymbolX, sideX, sig.QtyB, sig.PriceB, $"{clientOrderId}_X").ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PAIR_HYBRID OPEN {Pair} leg-X FAILED after leg-Y filled — IMBALANCE! Attempting leg-Y rollback", pair.Key);
                try
                {
                    var rollbackSide = sideY == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
                    await PlaceLeg(exchange, pair.SymbolY, rollbackSide, sig.QtyA, sig.PriceA, $"{clientOrderId}_Y_ROLLBACK").ConfigureAwait(false);
                    _logger.LogWarning("PAIR_HYBRID OPEN {Pair} leg-Y rolled back. Pair flat — no state recorded.", pair.Key);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogCritical(rollbackEx, "PAIR_HYBRID OPEN {Pair} ROLLBACK FAILED. MANUAL INTERVENTION REQUIRED on OKX demo.", pair.Key);
                    _ = _alerter?.SendAsync($"pair_rollback_fail_{pair.Key}", AlertSeverity.Critical,
                        "Pair Rollback FAILED — Manual Cleanup Needed",
                        $"Pair `{pair.Key}` leg-X rejected after leg-Y filled, AND rollback ALSO failed.\nOKX demo has an unhedged `{pair.SymbolY}` position.\nMUST manually close on OKX UI ASAP.",
                        CancellationToken.None);
                }
                return;
            }

            _logger.LogWarning("PAIR_HYBRID OPEN OK {Pair} legY={IdY} legX={IdX}", pair.Key, legYTrade?.Id, legXTrade?.Id);
        }

        var nowUtc = DateTime.UtcNow;
        _openPositions[pair.Key] = new OpenPairPosition(
            PairKey: pair.Key,
            SymbolY: pair.SymbolY,
            SymbolX: pair.SymbolX,
            Side: sig.Side,
            QtyA: sig.QtyA,
            QtyB: sig.QtyB,
            EntryPriceA: sig.PriceA,
            EntryPriceB: sig.PriceB,
            HedgeRatio: sig.HedgeRatio,
            EntryZScore: sig.EntryZScore,
            EntryTimeUtc: nowUtc,
            ClientOrderId: clientOrderId,
            ExecutionMode: _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN");

        // Mirror open into PaperPortfolio so paper_portfolio.json reflects live exposure.
        // Pair positions use the pair.Key (e.g. "BTC-USDT/ETH-USDT") as the Symbol; matched
        // on close by Symbol+EntryTime. Quantity stays 0 (pair-level notional is internal).
        var openMirror = new PaperPosition
        {
            Symbol = pair.Key,
            Side = sig.Side == PairsLiveSide.LongSpread ? TradeSide.Buy : TradeSide.Sell,
            EntryPrice = sig.PriceA,
            CurrentPrice = sig.PriceA,
            Quantity = 0m,
            EntryTime = nowUtc,
            StrategyTag = _strategyTag,
            RegimeTag = "PairsMeanReversion",
            ExecutionMode = _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN",
            PromotionStage = "Paper",
            IsLeveragedPaperPosition = true,
            Reasoning = $"Hybrid pairs open. β={sig.HedgeRatio:F3} entryZ={sig.EntryZScore:F2}",
            Confidence = 75,
        };
        lock (_portfolio) { _portfolio.OpenPositions.Add(openMirror); }
        _persistence?.RequestWrite(_portfolio);

        // [TCA persistence] mirror open into SQLite Trade table for TCA / dashboard.
        await PersistOpenAsync(pair, sig, clientOrderId, nowUtc).ConfigureAwait(false);
    }

    private async Task HandleCloseAsync(PairConfig pair, OpenPairPosition existing, PairsLiveSignal sig, IExchangeService? exchange, CancellationToken ct)
    {
        // Reverse both legs.
        var entrySideY = existing.Side == PairsLiveSide.LongSpread ? TradeSide.Buy : TradeSide.Sell;
        var entrySideX = existing.Side == PairsLiveSide.LongSpread ? TradeSide.Sell : TradeSide.Buy;
        var exitSideY = entrySideY == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        var exitSideX = entrySideX == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        var clientOrderId = $"PH_{pair.Key}_CLOSE_{DateTime.UtcNow:yyyyMMddHHmmss}";

        decimal pnlY = entrySideY == TradeSide.Buy
            ? (sig.PriceA - existing.EntryPriceA) * existing.QtyA
            : (existing.EntryPriceA - sig.PriceA) * existing.QtyA;
        decimal pnlX = entrySideX == TradeSide.Buy
            ? (sig.PriceB - existing.EntryPriceB) * existing.QtyB
            : (existing.EntryPriceB - sig.PriceB) * existing.QtyB;
        decimal grossPnL = pnlY + pnlX;
        decimal feeRate = 0.0005m; // 5bp/leg estimate
        decimal fees = (existing.EntryPriceA * existing.QtyA + sig.PriceA * existing.QtyA + existing.EntryPriceB * existing.QtyB + sig.PriceB * existing.QtyB) * feeRate;
        decimal netPnL = grossPnL - fees;

        _logger.LogWarning(
            "[{Mode}] PAIR_HYBRID CLOSE {Pair} | LegY {SymY} {ExitSideY} qty={QtyA:F6} entry={EY:F4} exit={XY:F4} | LegX {SymX} {ExitSideX} qty={QtyB:F6} entry={EX:F4} exit={XX:F4} | est gross={Gross:F2} fees={Fees:F2} net={Net:F2} reason={R}",
            _mode, pair.Key, pair.SymbolY, exitSideY, existing.QtyA, existing.EntryPriceA, sig.PriceA,
            pair.SymbolX, exitSideX, existing.QtyB, existing.EntryPriceB, sig.PriceB, grossPnL, fees, netPnL, sig.Reasoning);

        decimal? realFeePaidTotal = null;
        if (_mode == HybridMode.Live && exchange is not null)
        {
            try { await PlaceLeg(exchange, pair.SymbolY, exitSideY, existing.QtyA, sig.PriceA, $"{clientOrderId}_Y", reduceOnly: true).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "PAIR_HYBRID CLOSE {Pair} leg-Y FAILED", pair.Key); /* continue, try leg X */ }

            try { await PlaceLeg(exchange, pair.SymbolX, exitSideX, existing.QtyB, sig.PriceB, $"{clientOrderId}_X", reduceOnly: true).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "PAIR_HYBRID CLOSE {Pair} leg-X FAILED", pair.Key); }

            // Capture venue-reported fees on both legs (× 2 for entry+exit round-trip approximation).
            // Falls back to the estimate already in `fees` if the fill API doesn't return data.
            try
            {
                if (exchange is IExchangePositionReader reader)
                {
                    // Live close path — fill just placed, default 5-min window is sufficient.
                    var fillY = await reader.GetLastFillAsync(pair.SymbolY, cancellationToken: ct).ConfigureAwait(false);
                    var fillX = await reader.GetLastFillAsync(pair.SymbolX, cancellationToken: ct).ConfigureAwait(false);
                    decimal feeY = fillY?.FeePaid ?? 0m;
                    decimal feeX = fillX?.FeePaid ?? 0m;
                    if (feeY > 0m || feeX > 0m)
                    {
                        realFeePaidTotal = (feeY + feeX) * 2m; // double for entry-leg approximation
                        netPnL = grossPnL - realFeePaidTotal.Value;
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "PAIR_HYBRID CLOSE {Pair} fill-fee capture failed; using estimate.", pair.Key); }

            _logger.LogWarning("PAIR_HYBRID CLOSE attempted both legs. Reconcile via OKX position reader to verify.");
        }

        // Mirror to PaperPortfolio.
        var position = new PaperPosition
        {
            Symbol = pair.Key,
            Side = existing.Side == PairsLiveSide.LongSpread ? TradeSide.Buy : TradeSide.Sell,
            EntryPrice = existing.EntryPriceA,
            CurrentPrice = sig.PriceA,
            Quantity = 0m, // pair-level notional internal; leg sizing tracked separately
            EntryTime = existing.EntryTimeUtc,
            ExitTime = DateTime.UtcNow,
            RealizedPnL = netPnL,
            ExitReason = sig.Reasoning,
            Reasoning = $"Hybrid pairs close. β={existing.HedgeRatio:F3} entryZ={existing.EntryZScore:F2}",
            StrategyTag = _strategyTag,
            RegimeTag = "PairsMeanReversion",
            ExecutionMode = _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN",
            PromotionStage = "Paper",
            RealFeePaid = realFeePaidTotal
        };
        lock (_portfolio)
        {
            _portfolio.OpenPositions.RemoveAll(p => p.Symbol == pair.Key && p.EntryTime == existing.EntryTimeUtc);
            _portfolio.ClosedPositions.Add(position);
            _portfolio.Cash += netPnL;
            _portfolio.DailyPnL += netPnL;
            _portfolio.EquityCurve.Add(_portfolio.TotalEquity);
        }
        _persistence?.RequestWrite(_portfolio);

        // Daily PnL accounting + circuit breaker.
        _dailyRealizedPnL += netPnL;
        var basis = _portfolio.InitialBalance > 0m ? _portfolio.InitialBalance : 100_000m;
        var lossPct = _dailyRealizedPnL < 0m ? -_dailyRealizedPnL / basis : 0m;
        if (!_circuitBreakerTripped && lossPct >= _circuitBreakerLossPctOfBasis)
        {
            _circuitBreakerTripped = true;
            _logger.LogCritical(
                "CIRCUIT BREAKER TRIPPED (Pairs): daily loss ${Loss:F2} = {LossPct:P2} of basis ${Basis:F2} (threshold {Threshold:P2}).",
                _dailyRealizedPnL, lossPct, basis, _circuitBreakerLossPctOfBasis);
            await (_alerter?.SendAsync("circuit_breaker_pairs", AlertSeverity.Critical,
                "Pairs Circuit Breaker TRIPPED",
                $"Daily realized PnL: ${_dailyRealizedPnL:F2} = {lossPct:P2} of basis ${basis:F2}.\nThreshold {_circuitBreakerLossPctOfBasis:P2} breached.\nNew pair entries SUPPRESSED until UTC midnight. Existing positions still managed.",
                CancellationToken.None) ?? Task.CompletedTask).ConfigureAwait(false);
        }

        // Global cross-tier circuit breaker.
        if (_globalBreaker is not null)
        {
            var globalTrippedNow = _globalBreaker.RecordRealizedPnl($"Pairs:{_tierName}", netPnL, basis);
            if (globalTrippedNow)
            {
                await (_alerter?.SendAsync("global_circuit_breaker", AlertSeverity.Critical,
                    "GLOBAL Circuit Breaker TRIPPED",
                    $"Cross-tier daily PnL: ${_globalBreaker.DailyRealizedPnl:F2} = {(-_globalBreaker.DailyRealizedPnl/basis):P2} of basis ${basis:F2}.\nThreshold {_globalBreaker.Threshold:P2} breached via Pairs:{_tierName}.\nALL TIER ENTRIES SUPPRESSED until UTC midnight.",
                    CancellationToken.None) ?? Task.CompletedTask).ConfigureAwait(false);
            }
        }

        _openPositions.Remove(pair.Key);
        _notionalGuard?.Release($"Pairs:{_tierName}", pair.Key);

        // [TCA persistence] update SQLite Trade row with exit details.
        await PersistCloseAsync(pair, existing, sig, grossPnL, fees, netPnL).ConfigureAwait(false);
    }

    /// <summary>
    /// [TCA persistence] Persists a Trade row on pair-open so the SQLite Trade table
    /// has a record of every paper trade lifecycle. The Trade.Id is cached in
    /// <see cref="_tradeIdByPairLifecycle"/> keyed by pair.Key+entryTime so the
    /// matching close handler can update the same row.
    /// Notional and pnl-cardinal fields are populated from leg-A entry price as a
    /// representative price; full leg-level decomposition stays in PaperPortfolio.
    /// Side: LongSpread (buy A / sell B) → Buy ; ShortSpread → Sell.
    /// Failure is non-fatal — paper continues even if DB write fails.
    /// </summary>
    private async Task PersistOpenAsync(PairConfig pair, PairsLiveSignal sig, string clientOrderId, DateTime nowUtc)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetService<ITradeRepository>();
            if (repo is null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // Trade has no Metadata dict; encode pair-specific fields into ExitPolicyTag
            // as a compact JSON-ish blob so TCA harness can parse them. Free text Reasoning
            // holds a human-readable summary.
            var blob =
                $"{{pair:\"{pair.Key}\",legB:\"{pair.SymbolX}\",legB_entry:{sig.PriceB.ToString("F8", inv)}," +
                $"beta:{sig.HedgeRatio.ToString("F6", inv)},entryZ:{sig.EntryZScore.ToString("F4", inv)}," +
                $"clordid:\"{clientOrderId}\"}}";
            var trade = new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = Guid.NewGuid(),
                Symbol = pair.Key,
                Side = sig.Side == PairsLiveSide.LongSpread ? TradeSide.Buy : TradeSide.Sell,
                EntryPrice = sig.PriceA,
                Quantity = 0m,
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Active,
                StrategyTag = _strategyTag,
                RegimeTag = "PairsMeanReversion",
                ExitPolicyTag = blob,
                EntryTime = nowUtc,
                ExecutionSource = _mode == HybridMode.Live ? "OKX_DEMO" : "PAPER_DRYRUN",
                PromotionStage = "Paper",
                SignalPrice = sig.PriceA,
            };
            await repo.AddAsync(trade).ConfigureAwait(false);
            _tradeIdByPairLifecycle[$"{pair.Key}|{nowUtc.Ticks}"] = trade.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PairsHybrid: ITradeRepository AddAsync failed (open) for {Pair}", pair.Key);
        }
    }

    /// <summary>[TCA persistence] Update the matching Trade row on pair-close with exit + PnL.</summary>
    private async Task PersistCloseAsync(
        PairConfig pair, OpenPairPosition existing, PairsLiveSignal sig,
        decimal grossPnL, decimal fees, decimal netPnL)
    {
        try
        {
            var key = $"{pair.Key}|{existing.EntryTimeUtc.Ticks}";
            if (!_tradeIdByPairLifecycle.TryGetValue(key, out var tradeId))
            {
                _logger.LogWarning(
                    "PairsHybrid: no Trade.Id mapped for {Pair} entryTime={ET:o} — close NOT persisted to SQLite (likely worker restart mid-position).",
                    pair.Key, existing.EntryTimeUtc);
                return;
            }
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetService<ITradeRepository>();
            if (repo is null) return;
            var trade = await repo.GetByIdAsync(tradeId).ConfigureAwait(false);
            if (trade is null) return;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            trade.ExitPrice = sig.PriceA;
            trade.ExitTime = DateTime.UtcNow;
            trade.LifecycleStatus = TradeLifecycleStatus.Closed;
            trade.Status = TradeStatus.Executed;
            trade.TotalFeesPaid = fees;
            trade.ExitReason = sig.Reasoning ?? string.Empty;
            // Append close fields to the ExitPolicyTag blob (TCA harness reads both opens and closes).
            trade.ExitPolicyTag = trade.ExitPolicyTag.TrimEnd('}') +
                $",legB_exit:{sig.PriceB.ToString("F8", inv)}," +
                $"gross:{grossPnL.ToString("F4", inv)},fees:{fees.ToString("F4", inv)},net:{netPnL.ToString("F4", inv)}}}";

            await repo.UpdateAsync(trade).ConfigureAwait(false);
            _tradeIdByPairLifecycle.Remove(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PairsHybrid: ITradeRepository UpdateAsync failed (close) for {Pair}", pair.Key);
        }
    }

    private async Task<Trade?> PlaceLeg(IExchangeService exchange, string symbol, TradeSide side, decimal qty, decimal refPrice, string clientOrderId, bool reduceOnly = false)
    {
        var signal = new Signal
        {
            Symbol = symbol,
            Side = side,
            Quantity = qty,
            Price = refPrice,
            OrderType = OrderType.Market,
            Confidence = 75,
            StrategyTag = _strategyTag,
            Reasoning = "Pair leg"
        };
        signal.Metadata["ClientOrderId"] = clientOrderId;
        if (reduceOnly) signal.Metadata["ReduceOnly"] = "true";
        return await exchange.ExecuteOrderAsync(signal).ConfigureAwait(false);
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var json = File.ReadAllText(_statePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, OpenPairPosition>>(json);
            if (loaded is not null)
            {
                foreach (var kv in loaded) _openPositions[kv.Key] = kv.Value;
                _logger.LogInformation("PairsHybridExecutor: loaded {Count} open pair positions from state file.", _openPositions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PairsHybridExecutor: state load failed.");
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
            _logger.LogWarning(ex, "PairsHybridExecutor: state save failed.");
        }
    }

    public sealed record PairConfig(string SymbolY, string SymbolX, decimal WeightFraction = 0.10m)
    {
        public string Key => $"{SymbolY}-{SymbolX}";
    }

    public sealed record OpenPairPosition(
        string PairKey,
        string SymbolY,
        string SymbolX,
        PairsLiveSide Side,
        decimal QtyA,
        decimal QtyB,
        decimal EntryPriceA,
        decimal EntryPriceB,
        decimal HedgeRatio,
        double EntryZScore,
        DateTime EntryTimeUtc,
        string ClientOrderId,
        string ExecutionMode = "");

    private enum HybridMode { Off, DryRun, Live }
}
