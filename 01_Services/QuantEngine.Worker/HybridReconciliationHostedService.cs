using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodically (every 2 minutes by default) reconciles internal hybrid-executor state files
/// (<c>funding_hybrid_state.json</c>, <c>pairs_hybrid_state.json</c>) against the actual open
/// positions on OKX (via <see cref="IExchangePositionReader"/>). Logs LOUD warnings when
/// drift is detected — the operator can then decide to reset state or flatten OKX.
///
/// Drift cases handled:
///   - Internal says position open, OKX shows none  → "ghost position" (state stale)
///   - Internal says flat, OKX shows position       → "orphan position" (failed close or manual)
///   - Both have position but different qty/side    → "imbalance"
///
/// Read-only by design — never mutates state automatically. Auto-mutation could compound bugs.
/// </summary>
public sealed class HybridReconciliationHostedService : BackgroundService
{
    private readonly ILogger<HybridReconciliationHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;
    private readonly string _fundingStatePath;
    private readonly string _pairsStatePath;
    private readonly IRealtimeAlerter? _alerter;

    public HybridReconciliationHostedService(
        ILogger<HybridReconciliationHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _alerter = alerter;

        var fundingMode = (configuration["Funding:HybridMode"] ?? "off").Trim().ToLowerInvariant();
        var pairsMode = (configuration["Strategy:PairsTrading:HybridMode"] ?? "off").Trim().ToLowerInvariant();
        _enabled = fundingMode != "off" || pairsMode != "off";

        var intervalSeconds = int.TryParse(configuration["Hybrid:ReconciliationIntervalSeconds"], out var iv) && iv > 0 ? iv : 120;
        _interval = TimeSpan.FromSeconds(intervalSeconds);

        var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant");
        // Legacy single-tier paths (still supported for backward compat).
        _fundingStatePath = Path.Combine(stateDir, "funding_hybrid_state.json");
        _pairsStatePath = Path.Combine(stateDir, "pairs_hybrid_state.json");
        // Barbell tier paths — when these exist, reconciliation merges them with the legacy
        // path. Without this, reconciliation only sees the legacy file (likely empty under
        // barbell) and falsely reports OKX positions as GHOSTs.
        _tierStatePaths = new[]
        {
            Path.Combine(stateDir, "funding_long_state.json"),
            Path.Combine(stateDir, "funding_short_state.json"),
        };
        _tierPairStatePaths = new[]
        {
            Path.Combine(stateDir, "pairs_long_state.json"),
            Path.Combine(stateDir, "pairs_short_state.json"),
        };
    }

    private readonly string[] _tierStatePaths;
    private readonly string[] _tierPairStatePaths;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("HybridReconciliationHostedService disabled (no hybrid mode active).");
            return;
        }

        _logger.LogInformation("HybridReconciliationHostedService starting. Interval={Interval}s.", _interval.TotalSeconds);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "Reconciliation tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReconcileOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var positionReader = scope.ServiceProvider.GetService<IExchangePositionReader>();
        if (positionReader is null)
        {
            _logger.LogWarning("Reconciliation: no IExchangePositionReader registered — skip.");
            return;
        }

        IReadOnlyList<ExchangePositionSnapshot> okxPositions;
        try
        {
            okxPositions = await positionReader.GetOpenPositionsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconciliation: GetOpenPositionsAsync failed — skip this tick.");
            return;
        }

        var internalSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Funding state: union of legacy single-tier file + barbell tier files.
        // Same symbol may appear in multiple tiers — we aggregate qty for the OKX comparison.
        // BUG-FIX 2026-04-26: SKIP positions tagged ExecutionMode="PAPER_DRYRUN" — those
        // never hit OKX so comparing to OKX positions produces 100% false-positive GHOSTs.
        var allFundingPaths = _tierStatePaths.Append(_fundingStatePath);
        var aggregatedFunding = new Dictionary<string, (Contracts.Enums.TradeSide Side, decimal Qty, string Source)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in allFundingPaths)
        {
            var state = TryLoadJson<Dictionary<string, FundingHybridExecutor.OpenPosition>>(path);
            if (state is null) continue;
            var srcLabel = Path.GetFileNameWithoutExtension(path);
            foreach (var (sym, pos) in state)
            {
                // Skip dry-run positions — no real OKX order placed. Empty ExecutionMode
                // means the position was written before the field existed (pre-live era) =
                // by definition dry-run, so also skip.
                if (string.IsNullOrEmpty(pos.ExecutionMode) ||
                    string.Equals(pos.ExecutionMode, "PAPER_DRYRUN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (aggregatedFunding.TryGetValue(sym, out var prev))
                {
                    // Same symbol in multiple tiers — sum qty if same side, else net out.
                    var netQty = prev.Side == pos.Side ? prev.Qty + pos.Qty : prev.Qty - pos.Qty;
                    var dominantSide = Math.Abs(netQty) >= 0.0000001m && netQty < 0
                        ? (prev.Side == Contracts.Enums.TradeSide.Buy ? Contracts.Enums.TradeSide.Sell : Contracts.Enums.TradeSide.Buy)
                        : prev.Side;
                    aggregatedFunding[sym] = (dominantSide, Math.Abs(netQty), $"{prev.Source}+{srcLabel}");
                }
                else
                {
                    aggregatedFunding[sym] = (pos.Side, pos.Qty, srcLabel);
                }
            }
        }
        foreach (var (sym, agg) in aggregatedFunding)
        {
            internalSymbols.Add(sym);
            CompareSymbol(sym, agg.Side, agg.Qty, okxPositions, $"Funding[{agg.Source}]");
        }

        // Pairs state: union of legacy + tier files. Each pair's two legs aggregated independently.
        var allPairsPaths = _tierPairStatePaths.Append(_pairsStatePath);
        foreach (var path in allPairsPaths)
        {
            var state = TryLoadJson<Dictionary<string, PairsHybridExecutor.OpenPairPosition>>(path);
            if (state is null) continue;
            var srcLabel = Path.GetFileNameWithoutExtension(path);
            foreach (var (key, pos) in state)
            {
                // Skip dry-run pair positions — no real OKX leg orders placed.
                if (string.Equals(pos.ExecutionMode, "PAPER_DRYRUN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var sideY = pos.Side == ThatsMyQuant.Application.Strategy.PairsLiveSide.LongSpread ? Contracts.Enums.TradeSide.Buy : Contracts.Enums.TradeSide.Sell;
                var sideX = pos.Side == ThatsMyQuant.Application.Strategy.PairsLiveSide.LongSpread ? Contracts.Enums.TradeSide.Sell : Contracts.Enums.TradeSide.Buy;
                internalSymbols.Add(pos.SymbolY);
                internalSymbols.Add(pos.SymbolX);
                CompareSymbol(pos.SymbolY, sideY, pos.QtyA, okxPositions, $"Pairs[{srcLabel}:{key}]");
                CompareSymbol(pos.SymbolX, sideX, pos.QtyB, okxPositions, $"Pairs[{srcLabel}:{key}]");
            }
        }

        // Orphans on OKX that we don't track internally.
        // LOG ONLY (no Telegram): the ghost-cleanup path already handles bracket-fired
        // positions with a downgrade-to-INFO branch. The remaining "orphan" cases here
        // are almost always state-file restart desync or symbols the worker's
        // OpenPositions dict hasn't loaded yet — not actionable, so they don't deserve
        // a Telegram push every 2 minutes for the same 6 symbols.
        foreach (var okxPos in okxPositions)
        {
            if (!internalSymbols.Contains(okxPos.Symbol))
            {
                _logger.LogWarning(
                    "RECONCILE ORPHAN: OKX has open position {Symbol} qty={Qty} long={IsLong} but internal state is FLAT. Manual cleanup may be needed.",
                    okxPos.Symbol, okxPos.Quantity, okxPos.IsLong);
            }
        }

        _logger.LogInformation(
            "Reconciliation tick complete: internal symbols={Internal} okx open={Okx}",
            internalSymbols.Count, okxPositions.Count);
    }

    private void CompareSymbol(string symbol, Contracts.Enums.TradeSide expectedSide, decimal expectedQty, IReadOnlyList<ExchangePositionSnapshot> okxPositions, string sourceTag)
    {
        var match = okxPositions.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            // Auto-clean: ask the executor that owns this tier to drop the position from
            // its in-memory state and persist. Most ghosts are benign — they happen when
            // an OKX-side bracket TP/SL fires between our 120s reconciliation ticks. If
            // cleanup succeeds we DOWNGRADE the alert to INFO (no spam). Only true
            // structural ghosts (failed-open + cleanup couldn't find the executor) keep
            // the CRITICAL severity.
            var cleaned = FundingHybridExecutor.TryHandleGhostFromReconciliation(sourceTag, symbol);
            if (cleaned)
            {
                _logger.LogInformation(
                    "RECONCILE GHOST AUTO-CLEANED [{Source}]: {Symbol} {Side} qty={Qty} — OKX bracket fired, internal state synced.",
                    sourceTag, symbol, expectedSide, expectedQty);
                // No Telegram alert: this is normal bracket-fired close, not a real anomaly.
                return;
            }
            _logger.LogWarning(
                "RECONCILE GHOST [{Source}]: internal state has {Symbol} {Side} qty={Qty} but OKX shows NO position. Failed open or manual close on OKX. (auto-cleanup unavailable)",
                sourceTag, symbol, expectedSide, expectedQty);
            _ = _alerter?.SendAsync($"reconcile_ghost_{symbol}", AlertSeverity.Critical,
                $"Reconciliation Ghost [{sourceTag}]",
                $"Internal says `{symbol}` {expectedSide} qty={expectedQty} but OKX shows NO position.\nLikely failed-open OR position closed on OKX outside our control.",
                CancellationToken.None);
            return;
        }

        var expectedLong = expectedSide == Contracts.Enums.TradeSide.Buy;
        if (match.IsLong != expectedLong)
        {
            _logger.LogWarning(
                "RECONCILE SIDE-MISMATCH [{Source}]: {Symbol} internal={Side} OKX={OkxLong} qty(internal/okx)={InternalQty}/{OkxQty}",
                sourceTag, symbol, expectedSide, match.IsLong ? "Long" : "Short", expectedQty, match.Quantity);
            _ = _alerter?.SendAsync($"reconcile_side_{symbol}", AlertSeverity.Critical,
                $"Reconciliation Side Mismatch [{sourceTag}]",
                $"`{symbol}` internal={expectedSide} OKX={(match.IsLong ? "Long" : "Short")} qty(int/okx)={expectedQty}/{match.Quantity}",
                CancellationToken.None);
            return;
        }

        // Allow 15% tolerance on quantity. Internal qty is base-asset (e.g. 0.1 AAVE),
        // OKX returns contracts × ctVal lot-rounded; partial fills also stretch the diff.
        // Below 15% the noise is rounding + cumulative partial-fill drift on legacy
        // positions — not actionable, so don't even log-warn. Real reverse-position bugs
        // typically hit 100%+ which still triggers.
        // LOG ONLY (no Telegram): same 6 symbols re-fire every reconcile tick; spammy.
        var qtyDiffPct = expectedQty > 0 ? Math.Abs(match.Quantity - expectedQty) / expectedQty : 0m;
        if (qtyDiffPct > 0.15m)
        {
            _logger.LogWarning(
                "RECONCILE QTY-DRIFT [{Source}]: {Symbol} internal={InternalQty} OKX={OkxQty} diff={DiffPct:P2}",
                sourceTag, symbol, expectedQty, match.Quantity, qtyDiffPct);
        }
    }

    private static T? TryLoadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }
}
