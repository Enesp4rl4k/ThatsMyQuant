using ThatsMyQuant.Contracts.Models;

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
        public double UlcerIndex { get; init; }
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
        var winAmounts = pnls.Where(p => p > 0).ToList();
        var lossAmounts = pnls.Where(p => p < 0).ToList();

        decimal avgWin = winAmounts.Any() ? winAmounts.Average() : 0;
        decimal avgLoss = lossAmounts.Any() ? lossAmounts.Average() : 0;
        double winRate = (double)winAmounts.Count / closed.Count;

        var (maxDD, maxDDPercent) = CalculateMaxDrawdown(portfolio);

        return new PerformanceReport
        {
            TotalTrades = closed.Count,
            WinningTrades = winAmounts.Count,
            LosingTrades = lossAmounts.Count,
            WinRate = winRate * 100,
            TotalPnL = pnls.Sum(),
            SharpeRatio = CalculateSharpeRatio(pnls),
            SortinoRatio = CalculateSortinoRatio(pnls),
            ProfitFactor = CalculateProfitFactor(winAmounts, lossAmounts),
            AverageRR = avgLoss != 0 ? (double)(avgWin / Math.Abs(avgLoss)) : 0,
            MaxDrawdown = maxDD,
            MaxDrawdownPercent = maxDDPercent,
            UlcerIndex = CalculateUlcerIndex(portfolio),
            MaxConsecutiveLosses = CalculateMaxConsecutiveLosses(closed),
            BestTrade = pnls.Max(),
            WorstTrade = pnls.Min(),
            AverageWin = avgWin,
            AverageLoss = avgLoss,
            ExpectedValue = winRate * (double)avgWin + (1 - winRate) * (double)avgLoss,
            AverageHoldTime = CalculateAverageHoldTime(closed)
        };
    }

    private static double CalculateSharpeRatio(List<decimal> pnls)
    {
        if (pnls.Count < 2) return 0;

        var doublePnls = pnls.Select(p => (double)p).ToList();
        double avg = doublePnls.Average();
        double stdDev = Math.Sqrt(doublePnls.Sum(p => Math.Pow(p - avg, 2)) / (doublePnls.Count - 1));
        
        return stdDev > 0 ? avg / stdDev * Math.Sqrt(252) : 0;
    }

    private static double CalculateSortinoRatio(List<decimal> pnls)
    {
        if (!pnls.Any()) return 0;

        var doublePnls = pnls.Select(p => (double)p).ToList();
        double avg = doublePnls.Average();
        
        var downside = doublePnls.Where(p => p < 0).ToList();
        if (!downside.Any()) return avg > 0 ? 99 : 0;

        double downDev = Math.Sqrt(downside.Sum(p => Math.Pow(p, 2)) / downside.Count);
        return downDev > 0 ? avg / downDev * Math.Sqrt(252) : 0;
    }

    private static double CalculateProfitFactor(List<decimal> wins, List<decimal> losses)
    {
        decimal grossProfit = wins.Sum();
        decimal grossLoss = Math.Abs(losses.Sum());
        
        if (grossLoss <= 0) return grossProfit > 0 ? 99 : 0;
        return (double)(grossProfit / grossLoss);
    }

    private static (decimal MaxDD, decimal MaxDDPercent) CalculateMaxDrawdown(PaperPortfolio portfolio)
    {
        decimal maxDD = 0, maxDDPercent = 0, peak = portfolio.InitialBalance;
        foreach (var eq in portfolio.EquityCurve)
        {
            if (eq > peak) peak = eq;
            decimal dd = peak - eq;
            if (dd > maxDD)
            {
                maxDD = dd;
                maxDDPercent = peak > 0 ? dd / peak * 100 : 0;
            }
        }
        return (maxDD, maxDDPercent);
    }

    /// <summary>
    /// Ulcer Index — measures the depth and duration of drawdowns.
    /// UI = sqrt(mean(DD%^2)) where DD% is the percentage drawdown from peak at each point.
    /// Lower is better. UI &lt; 5 = smooth equity curve; UI &gt; 15 = volatile/painful.
    /// </summary>
    private static double CalculateUlcerIndex(PaperPortfolio portfolio)
    {
        if (portfolio.EquityCurve.Count < 2) return 0;

        decimal peak = portfolio.InitialBalance;
        double sumSquaredDD = 0;
        int count = 0;

        foreach (var eq in portfolio.EquityCurve)
        {
            if (eq > peak) peak = eq;
            if (peak > 0)
            {
                double ddPercent = (double)((peak - eq) / peak * 100m);
                sumSquaredDD += ddPercent * ddPercent;
            }
            count++;
        }

        return count > 0 ? Math.Sqrt(sumSquaredDD / count) : 0;
    }

    private static int CalculateMaxConsecutiveLosses(IReadOnlyList<PaperPosition> positions)
    {
        int consLosses = 0, maxConsLosses = 0;
        foreach (var p in positions)
        {
            if (p.RealizedPnL < 0)
            {
                consLosses++;
                maxConsLosses = Math.Max(maxConsLosses, consLosses);
            }
            else
            {
                consLosses = 0;
            }
        }
        return maxConsLosses;
    }

    private static TimeSpan CalculateAverageHoldTime(IReadOnlyList<PaperPosition> positions)
    {
        var holdTimes = positions
            .Where(p => p.ExitTime.HasValue)
            .Select(p => p.ExitTime!.Value - p.EntryTime)
            .ToList();

        return holdTimes.Any()
            ? TimeSpan.FromTicks((long)holdTimes.Average(t => t.Ticks))
            : TimeSpan.Zero;
    }
}
