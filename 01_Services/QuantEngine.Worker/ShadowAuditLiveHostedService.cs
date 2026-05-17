using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodic shadow execution audit on the rolling window of recently closed paper trades,
/// per-symbol. Re-prices each trade through <see cref="ExecutionImpactModel"/>
/// (square-root impact + spread + commission) and compares the realistic "shadow" PnL
/// to the recorded paper PnL.
///
/// Output is read-only — never modifies positions. Logs verdict (Pass / Warn / Reject /
/// Critical) per symbol, alerts via <see cref="IRealtimeAlerter"/> when verdict is
/// Reject or Critical or aggregate leakage exceeds threshold. Tells us whether alpha
/// decay is from EDGE failure or EXECUTION failure (different fixes).
/// </summary>
public sealed class ShadowAuditLiveHostedService : BackgroundService
{
    private readonly ILogger<ShadowAuditLiveHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;
    private readonly IRealtimeAlerter? _alerter;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _windowTrades;
    private readonly int _candleLookback;
    private readonly string _timeframe;
    private readonly double _alertLeakagePct;
    private DateTime _lastAuditAt = DateTime.MinValue;

    public ShadowAuditLiveHostedService(
        ILogger<ShadowAuditLiveHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _alerter = alerter;
        _enabled = !bool.TryParse(configuration["ShadowAudit:Enabled"], out var en) || en;
        var minutes = int.TryParse(configuration["ShadowAudit:IntervalMinutes"], out var m) && m > 0 ? m : 60;
        _interval = TimeSpan.FromMinutes(minutes);
        _windowTrades = int.TryParse(configuration["ShadowAudit:WindowTrades"], out var w) && w > 0 ? w : 30;
        _candleLookback = int.TryParse(configuration["ShadowAudit:CandleLookback"], out var cl) && cl > 0 ? cl : 500;
        _timeframe = configuration["ShadowAudit:Timeframe"] ?? "1h";
        _alertLeakagePct = double.TryParse(configuration["ShadowAudit:AlertLeakagePercent"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 25.0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("ShadowAuditLiveHostedService disabled (ShadowAudit:Enabled=false).");
            return;
        }
        _logger.LogWarning(
            "ShadowAuditLive starting. Interval={I}m WindowTrades={W} Lookback={LB} Timeframe={TF} AlertLeakage>{A:F1}%",
            _interval.TotalMinutes, _windowTrades, _candleLookback, _timeframe, _alertLeakagePct);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { await RunAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "ShadowAudit tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        IReadOnlyList<PaperPosition> recent;
        lock (_portfolio)
        {
            var n = _portfolio.ClosedPositions.Count;
            if (n == 0) { _logger.LogDebug("ShadowAudit: no closed positions yet."); return; }
            var skip = Math.Max(0, n - _windowTrades);
            // Only audit trades that closed AFTER the last audit run (avoid double-alerting).
            recent = _portfolio.ClosedPositions
                .Skip(skip)
                .Where(p => (p.ExitTime ?? p.EntryTime) > _lastAuditAt)
                .ToList();
        }
        if (recent.Count == 0) { _logger.LogDebug("ShadowAudit: no new closed trades since last run."); return; }

        using var scope = _serviceProvider.CreateScope();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();
        var auditor = scope.ServiceProvider.GetRequiredService<IShadowExecutionAuditService>();

        var bySymbol = recent.GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase);
        decimal aggregateBacktest = 0m, aggregateShadow = 0m;
        int aggregateTrades = 0;

        foreach (var group in bySymbol)
        {
            if (ct.IsCancellationRequested) return;
            var symbol = group.Key;
            var trades = group.Select(ToBacktestTrade).ToList();

            List<Candle> candles;
            try
            {
                candles = await marketData.GetCandlesAsync(symbol, _candleLookback, "Crypto", _timeframe).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ShadowAudit: candle fetch failed for {Sym}", symbol);
                continue;
            }
            if (candles == null || candles.Count == 0)
            {
                _logger.LogDebug("ShadowAudit: no candles for {Sym}", symbol);
                continue;
            }

            var synthetic = new BacktestResult
            {
                Symbol = symbol,
                Trades = trades,
                TotalTrades = trades.Count,
                StartDate = trades.Min(t => t.EntryTime),
                EndDate = trades.Max(t => t.ExitTime),
                TotalPnL = trades.Sum(t => t.PnL)
            };

            var report = auditor.Audit(synthetic, candles);

            // [2026-05-13] Was logging "verdict=Pass" with trades=1 / trades=2 — single-trade
            // verdicts are statistically meaningless (one outlier flips the leakage). Require
            // at least 5 trades per symbol before producing a verdict; below that, log at
            // INFO level for visibility without misleading the operator.
            // [2026-05-14 BUG #1 FIX] Also EXCLUDE small-N from aggregate accumulators.
            // Otherwise XRPUSDT trades=1 + DOTUSDT trades=2 contaminate the aggregate
            // leakage (-178.8% nonsense from 7-symbol mix-of-noise-and-signal).
            const int MinTradesForVerdict = 5;
            if (report.TradesEvaluated < MinTradesForVerdict)
            {
                _logger.LogInformation(
                    "SHADOW_AUDIT {Sym} | trades={N} (< {Min}, sample too small for verdict) backtestPnL=${B:F2} shadowPnL=${S:F2}",
                    symbol, report.TradesEvaluated, MinTradesForVerdict, report.BacktestTotalPnL, report.ShadowTotalPnL);
                continue;   // skip aggregate too — small-N is noise that shouldn't pollute the universe-wide leakage stat
            }

            aggregateBacktest += report.BacktestTotalPnL;
            aggregateShadow += report.ShadowTotalPnL;
            aggregateTrades += report.TradesEvaluated;

            _logger.LogWarning(
                "SHADOW_AUDIT {Sym} | trades={N} backtestPnL=${B:F2} shadowPnL=${S:F2} leakage={L:F1}% verdict={V} | {Rec}",
                symbol, report.TradesEvaluated, report.BacktestTotalPnL, report.ShadowTotalPnL,
                report.LeakagePercent, report.Verdict, report.Recommendation);

            if ((report.Verdict == ShadowAuditVerdict.Reject || report.Verdict == ShadowAuditVerdict.Critical)
                && _alerter is not null)
            {
                await _alerter.SendAsync(
                    key: $"shadow_audit_{symbol}_{report.Verdict}",
                    severity: report.Verdict == ShadowAuditVerdict.Critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                    title: $"Shadow audit {report.Verdict} on {symbol}",
                    body:
                        $"Trades: {report.TradesEvaluated}\n" +
                        $"Backtest PnL: ${report.BacktestTotalPnL:F2}\nShadow PnL: ${report.ShadowTotalPnL:F2}\n" +
                        $"Leakage: {report.LeakagePercent:F1}% (avg {report.AverageLeakageBpsPerTrade:F1} bps/trade)\n\n" +
                        $"Recommendation: {report.Recommendation}",
                    ct: ct).ConfigureAwait(false);
            }
        }

        if (aggregateTrades > 0)
        {
            var aggLeakPct = aggregateBacktest != 0
                ? (double)((aggregateBacktest - aggregateShadow) / Math.Abs(aggregateBacktest)) * 100.0
                : 0.0;
            _logger.LogWarning(
                "SHADOW_AUDIT aggregate | symbols={S} trades={N} backtestPnL=${B:F2} shadowPnL=${Sh:F2} leakage={L:F1}%",
                bySymbol.Count(), aggregateTrades, aggregateBacktest, aggregateShadow, aggLeakPct);

            if (aggLeakPct > _alertLeakagePct && _alerter is not null)
            {
                await _alerter.SendAsync(
                    key: "shadow_audit_aggregate_high",
                    severity: AlertSeverity.Warning,
                    title: "Shadow audit: aggregate execution leakage high",
                    body:
                        $"Across {aggregateTrades} new trades on {bySymbol.Count()} symbols:\n" +
                        $"Backtest PnL: ${aggregateBacktest:F2}\nShadow PnL: ${aggregateShadow:F2}\n" +
                        $"Leakage: {aggLeakPct:F1}% (threshold {_alertLeakagePct:F1}%).\n\n" +
                        "Edge → execution gap is widening. Investigate maker/taker split, slippage, fill venues.",
                    ct: ct).ConfigureAwait(false);
            }
        }

        _lastAuditAt = DateTime.UtcNow;
    }

    private static BacktestTradeResult ToBacktestTrade(PaperPosition p)
    {
        var qty = p.Quantity > 0 ? p.Quantity : Math.Max(p.OpenedQuantity, 1m);
        var exitPx = p.CurrentPrice > 0 ? p.CurrentPrice : p.EntryPrice;
        var pnlPct = p.EntryPrice > 0 ? (p.RealizedPnL / (p.EntryPrice * qty)) * 100m : 0m;
        return new BacktestTradeResult
        {
            EntryTime = p.EntryTime,
            ExitTime = p.ExitTime ?? DateTime.UtcNow,
            Side = p.Side,
            StrategyTag = string.IsNullOrEmpty(p.StrategyTag) ? "Unknown" : p.StrategyTag,
            RegimeTag = string.IsNullOrEmpty(p.RegimeTag) ? "Unknown" : p.RegimeTag,
            ExitPolicyTag = string.IsNullOrEmpty(p.ExitPolicyTag) ? "Unknown" : p.ExitPolicyTag,
            EntryPrice = p.EntryPrice,
            ExitPrice = exitPx,
            PnL = p.RealizedPnL,
            PnLPercent = pnlPct,
            ExitReason = p.ExitReason ?? "",
            Reasoning = p.Reasoning ?? "",
            NetFunding = p.NetFunding
        };
    }
}
