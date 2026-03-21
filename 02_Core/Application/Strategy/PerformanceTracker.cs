using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Rolling performance tracker — computes real-time performance metrics
/// from a portfolio's trade history.
/// </summary>
public static class PerformanceTracker
{
    public record PerformanceReport
    {
        public int TotalTrades { get; init; }
        public int WinningTrades { get; init; }
        public int LosingTrades { get; init; }
        public double WinRate { get; init; }
        public decimal TotalPnL { get; init; }
        public double SharpeRatio { get; init; }
        public double SortinoRatio { get; init; }
        public double ProfitFactor { get; init; }
        public double AverageRR { get; init; }
        public decimal MaxDrawdown { get; init; }
        public decimal MaxDrawdownPercent { get; init; }
        public int MaxConsecutiveLosses { get; init; }
        public decimal BestTrade { get; init; }
        public decimal WorstTrade { get; init; }
        public decimal AverageWin { get; init; }
        public decimal AverageLoss { get; init; }
        public double ExpectedValue { get; init; }
        public TimeSpan AverageHoldTime { get; init; }
    }

    public static PerformanceReport ComputeFromPortfolio(PaperPortfolio portfolio)
    {
        var closed = portfolio.ClosedPositions;
        if (closed.Count == 0) return new PerformanceReport();

        var pnls = closed.Select(p => p.RealizedPnL).ToList();
        int wins = closed.Count(p => p.RealizedPnL > 0);
        int losses = closed.Count(p => p.RealizedPnL <= 0);

        var winAmounts = pnls.Where(p => p > 0).ToList();
        var lossAmounts = pnls.Where(p => p < 0).ToList();

        decimal avgWin = winAmounts.Count > 0 ? winAmounts.Average() : 0;
        decimal avgLoss = lossAmounts.Count > 0 ? lossAmounts.Average() : 0;

        // Sharpe
        var doublePnls = pnls.Select(p => (double)p).ToList();
        double avg = doublePnls.Average();
        double stdDev = doublePnls.Count > 1
            ? Math.Sqrt(doublePnls.Sum(p => Math.Pow(p - avg, 2)) / (doublePnls.Count - 1))
            : 0;
        double sharpe = stdDev > 0 ? avg / stdDev * Math.Sqrt(252) : 0;

        // Sortino
        var downside = doublePnls.Where(p => p < 0).ToList();
        double downDev = downside.Count > 0
            ? Math.Sqrt(downside.Sum(p => Math.Pow(p, 2)) / downside.Count)
            : 0;
        double sortino = downDev > 0 ? avg / downDev * Math.Sqrt(252) : avg > 0 ? 99 : 0;

        // Profit Factor
        decimal grossProfit = pnls.Where(p => p > 0).Sum();
        decimal grossLoss = Math.Abs(pnls.Where(p => p < 0).Sum());
        double profitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : grossProfit > 0 ? 99 : 0;

        // Average RR
        double avgRR = avgLoss != 0 ? (double)(avgWin / Math.Abs(avgLoss)) : 0;

        // Max drawdown from equity curve
        decimal maxDD = 0, maxDDPercent = 0, peak = portfolio.InitialBalance;
        foreach (var eq in portfolio.EquityCurve)
        {
            if (eq > peak) peak = eq;
            decimal dd = peak - eq;
            if (dd > maxDD) { maxDD = dd; maxDDPercent = peak > 0 ? dd / peak * 100 : 0; }
        }

        // Max consecutive losses
        int consLosses = 0, maxConsLosses = 0;
        foreach (var p in closed)
        {
            if (p.RealizedPnL < 0) { consLosses++; maxConsLosses = Math.Max(maxConsLosses, consLosses); }
            else consLosses = 0;
        }

        // Expected value
        double winRate = closed.Count > 0 ? wins * 1.0 / closed.Count : 0;
        double ev = winRate * (double)avgWin + (1 - winRate) * (double)avgLoss;

        // Average hold time
        var holdTimes = closed.Where(p => p.ExitTime.HasValue)
            .Select(p => p.ExitTime!.Value - p.EntryTime).ToList();
        var avgHold = holdTimes.Count > 0
            ? TimeSpan.FromTicks((long)holdTimes.Average(t => t.Ticks))
            : TimeSpan.Zero;

        return new PerformanceReport
        {
            TotalTrades = closed.Count,
            WinningTrades = wins,
            LosingTrades = losses,
            WinRate = winRate * 100,
            TotalPnL = pnls.Sum(),
            SharpeRatio = sharpe,
            SortinoRatio = sortino,
            ProfitFactor = profitFactor,
            AverageRR = avgRR,
            MaxDrawdown = maxDD,
            MaxDrawdownPercent = maxDDPercent,
            MaxConsecutiveLosses = maxConsLosses,
            BestTrade = pnls.Count > 0 ? pnls.Max() : 0,
            WorstTrade = pnls.Count > 0 ? pnls.Min() : 0,
            AverageWin = avgWin,
            AverageLoss = avgLoss,
            ExpectedValue = ev,
            AverageHoldTime = avgHold
        };
    }
}
