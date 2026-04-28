using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Re-walks every backtest trade through ExecutionImpactModel + spread + commission and
/// compares the realistic ("shadow") PnL against the backtest's optimistic PnL. The gap
/// is the slippage illusion the headline Sharpe was hiding.
/// </summary>
public sealed class ShadowExecutionAuditService : IShadowExecutionAuditService
{
    private readonly ILogger<ShadowExecutionAuditService> _logger;

    public ShadowExecutionAuditService(ILogger<ShadowExecutionAuditService> logger)
    {
        _logger = logger;
    }

    public ShadowAuditReport Audit(
        BacktestResult backtest,
        IReadOnlyList<Candle> candles,
        ShadowAuditOptions? options = null)
    {
        var opts = options ?? ShadowAuditOptions.Default;

        if (backtest.Trades.Count == 0 || candles.Count == 0)
        {
            return Empty(ShadowAuditVerdict.Insufficient,
                "No trades or candles supplied to shadow audit.");
        }

        var orderedCandles = candles
            .OrderBy(c => c.Timestamp)
            .ToArray();

        var comparisons = new List<ShadowTradeComparison>(backtest.Trades.Count);
        int unmatched = 0;
        decimal totalBacktestPnL = 0m;
        decimal totalShadowPnL = 0m;
        decimal totalLeakageDollar = 0m;
        decimal totalLeakageBps = 0m;
        int backtestWinners = 0;
        int shadowWinners = 0;

        foreach (var trade in backtest.Trades)
        {
            int entryIdx = FindCandleIndex(orderedCandles, trade.EntryTime);
            int exitIdx = FindCandleIndex(orderedCandles, trade.ExitTime);
            if (entryIdx < 0 || exitIdx < 0)
            {
                unmatched++;
                continue;
            }

            decimal notional = EstimateNotional(trade, opts);
            if (notional <= 0 || trade.EntryPrice <= 0)
            {
                unmatched++;
                continue;
            }
            decimal quantity = notional / trade.EntryPrice;

            var entryCandle = orderedCandles[entryIdx];
            var exitCandle = orderedCandles[exitIdx];
            decimal entryDailyVolume = ComputeDailyVolume(orderedCandles, entryIdx, opts.VolumeWindowBarsForDailyVolume);
            decimal exitDailyVolume = ComputeDailyVolume(orderedCandles, exitIdx, opts.VolumeWindowBarsForDailyVolume);
            decimal entryVolPct = ComputeBarVolatility(entryCandle, opts.DefaultDailyVolatilityPct);
            decimal exitVolPct = ComputeBarVolatility(exitCandle, opts.DefaultDailyVolatilityPct);

            decimal shadowEntry = ExecutionImpactModel.CalculateSquareRootImpactPrice(
                trade.EntryPrice, trade.Side, quantity, entryDailyVolume, entryVolPct);
            shadowEntry = ApplyExtraCosts(shadowEntry, trade.Side, opts);

            var exitSide = trade.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
            decimal shadowExit = ExecutionImpactModel.CalculateSquareRootImpactPrice(
                trade.ExitPrice, exitSide, quantity, exitDailyVolume, exitVolPct);
            shadowExit = ApplyExtraCosts(shadowExit, exitSide, opts);

            decimal pnlPerUnit = trade.Side == TradeSide.Buy
                ? shadowExit - shadowEntry
                : shadowEntry - shadowExit;
            decimal shadowPnL = pnlPerUnit * quantity;

            decimal leakageDollar = trade.PnL - shadowPnL;
            decimal leakageBps = notional > 0 ? leakageDollar / notional * 10_000m : 0m;

            comparisons.Add(new ShadowTradeComparison(
                EntryTime: trade.EntryTime,
                Strategy: trade.StrategyTag,
                BacktestEntryPrice: trade.EntryPrice,
                ShadowEntryPrice: shadowEntry,
                BacktestExitPrice: trade.ExitPrice,
                ShadowExitPrice: shadowExit,
                BacktestPnL: trade.PnL,
                ShadowPnL: shadowPnL,
                LeakageDollar: leakageDollar,
                LeakageBps: leakageBps));

            totalBacktestPnL += trade.PnL;
            totalShadowPnL += shadowPnL;
            totalLeakageDollar += leakageDollar;
            totalLeakageBps += leakageBps;
            if (trade.PnL > 0) backtestWinners++;
            if (shadowPnL > 0) shadowWinners++;
        }

        int n = comparisons.Count;
        if (n == 0)
        {
            return Empty(ShadowAuditVerdict.Insufficient,
                $"All {backtest.Trades.Count} trades fell outside the candle window.");
        }

        double leakagePct = totalBacktestPnL != 0
            ? (double)(totalLeakageDollar / Math.Abs(totalBacktestPnL)) * 100.0
            : 0.0;

        double bSharpe = ComputeSharpe(comparisons.Select(c => c.BacktestPnL));
        double sSharpe = ComputeSharpe(comparisons.Select(c => c.ShadowPnL));
        double bPf = ComputeProfitFactor(comparisons.Select(c => c.BacktestPnL));
        double sPf = ComputeProfitFactor(comparisons.Select(c => c.ShadowPnL));
        decimal avgLeakBps = totalLeakageBps / n;

        var (verdict, recommendation) = Decide(totalBacktestPnL, totalShadowPnL, leakagePct, opts);

        var report = new ShadowAuditReport(
            TradesEvaluated: n,
            TradesUnmatched: unmatched,
            BacktestTotalPnL: totalBacktestPnL,
            ShadowTotalPnL: totalShadowPnL,
            LeakageDollar: totalLeakageDollar,
            LeakagePercent: leakagePct,
            BacktestWinners: backtestWinners,
            ShadowWinners: shadowWinners,
            BacktestWinRate: backtestWinners * 100.0 / n,
            ShadowWinRate: shadowWinners * 100.0 / n,
            BacktestSharpe: bSharpe,
            ShadowSharpe: sSharpe,
            BacktestProfitFactor: bPf,
            ShadowProfitFactor: sPf,
            AverageLeakageBpsPerTrade: avgLeakBps,
            Verdict: verdict,
            Recommendation: recommendation,
            TradeDetails: comparisons);

        _logger.LogInformation(
            "ShadowAudit done | symbol={Symbol} trades={N} unmatched={U} backtestPnL={B:F2} shadowPnL={S:F2} leakage={L:F1}% verdict={V}",
            backtest.Symbol, n, unmatched, totalBacktestPnL, totalShadowPnL, leakagePct, verdict);

        return report;
    }

    private static int FindCandleIndex(Candle[] candles, DateTime timestamp)
    {
        // Largest index where candles[i].Timestamp <= timestamp.
        int lo = 0, hi = candles.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (candles[mid].Timestamp <= timestamp)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }

    private static decimal EstimateNotional(BacktestTradeResult trade, ShadowAuditOptions opts)
    {
        if (trade.PnLPercent != 0 && trade.PnL != 0)
        {
            decimal inferred = Math.Abs(trade.PnL / (trade.PnLPercent / 100m));
            if (inferred > 0) return inferred;
        }
        return opts.AssumedNotionalPerTrade;
    }

    private static decimal ComputeDailyVolume(Candle[] candles, int idx, int windowBars)
    {
        if (windowBars <= 0) return candles[idx].Volume;
        int half = windowBars / 2;
        int start = Math.Max(0, idx - half);
        int end = Math.Min(candles.Length - 1, idx + half);
        decimal sum = 0m;
        for (int i = start; i <= end; i++) sum += candles[i].Volume;
        int actual = end - start + 1;
        if (actual <= 0) return 0m;
        // Scale up to a full window when we hit edges so daily-volume isn't artificially small.
        return sum * windowBars / actual;
    }

    private static decimal ComputeBarVolatility(Candle c, decimal fallback)
    {
        if (c.Close <= 0) return fallback;
        decimal range = c.High - c.Low;
        return range > 0 ? range / c.Close : fallback;
    }

    private static decimal ApplyExtraCosts(decimal price, TradeSide side, ShadowAuditOptions opts)
    {
        decimal extraBps = opts.AdditionalSpreadBps + opts.AdditionalCommissionBps;
        decimal extra = price * (extraBps / 10_000m);
        return side == TradeSide.Buy ? price + extra : price - extra;
    }

    private static double ComputeSharpe(IEnumerable<decimal> pnlSeries)
    {
        var arr = pnlSeries.Select(x => (double)x).ToArray();
        if (arr.Length < 2) return 0.0;
        double mean = arr.Average();
        double sumSq = 0.0;
        foreach (var x in arr) sumSq += (x - mean) * (x - mean);
        double std = Math.Sqrt(sumSq / (arr.Length - 1));
        if (std <= 0) return 0.0;
        // Per-trade Sharpe scaled by √N — matches BacktestResult's convention.
        return mean / std * Math.Sqrt(arr.Length);
    }

    private static double ComputeProfitFactor(IEnumerable<decimal> pnls)
    {
        decimal gain = 0m, loss = 0m;
        foreach (var p in pnls)
        {
            if (p > 0) gain += p;
            else loss += -p;
        }
        if (loss == 0m) return gain > 0m ? double.PositiveInfinity : 0.0;
        return (double)(gain / loss);
    }

    private static (ShadowAuditVerdict, string) Decide(
        decimal backtestPnL,
        decimal shadowPnL,
        double leakagePct,
        ShadowAuditOptions opts)
    {
        if (backtestPnL > 0 && shadowPnL <= 0)
        {
            return (ShadowAuditVerdict.Critical,
                "Backtest profit is fully attributable to optimistic fills — strategy has no real edge under realistic execution. DO NOT promote.");
        }
        if (leakagePct >= opts.RejectLeakageThresholdPct)
        {
            return (ShadowAuditVerdict.Reject,
                $"Shadow PnL leaks {leakagePct:F1}% of backtest. Reduce size, prefer maker entries, or widen edge before promotion.");
        }
        if (leakagePct >= opts.WarnLeakageThresholdPct)
        {
            return (ShadowAuditVerdict.Warn,
                $"Shadow PnL leaks {leakagePct:F1}% of backtest — promote with caution and monitor execution closely.");
        }
        return (ShadowAuditVerdict.Pass,
            $"Shadow leakage {leakagePct:F1}% within tolerance. Edge is robust to realistic execution costs.");
    }

    private static ShadowAuditReport Empty(ShadowAuditVerdict verdict, string recommendation) =>
        new(0, 0, 0m, 0m, 0m, 0.0, 0, 0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0m, verdict, recommendation, Array.Empty<ShadowTradeComparison>());
}
