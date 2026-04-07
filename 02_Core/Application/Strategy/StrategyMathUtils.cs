using System;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

internal static class StrategyMathUtils
{
    internal static string ResolveSessionBucket(DateTime timestamp)
    {
        var turkeyTime = timestamp.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp.ToUniversalTime(), "Turkey Standard Time");

        return turkeyTime.Hour switch
        {
            >= 10 and < 13 => "London Killzone",
            >= 15 and < 18 => "NY Killzone",
            >= 3 and < 10 => "Asia",
            _ => "Close / Dead Zone"
        };
    }

    internal static double ComputeProfitFactor(IEnumerable<decimal> pnlValues)
    {
        var pnlList = pnlValues.ToList();
        if (pnlList.Count == 0)
        {
            return 0;
        }

        var grossProfit = pnlList.Where(pnl => pnl > 0).Sum();
        var grossLoss = Math.Abs(pnlList.Where(pnl => pnl < 0).Sum());
        if (grossLoss <= 0)
        {
            return grossProfit > 0 ? 99 : 0;
        }

        return (double)(grossProfit / grossLoss);
    }

    internal static double ComputeUlcerIndex(IEnumerable<decimal> equityCurve)
    {
        var equityList = equityCurve.ToList();
        if (equityList.Count < 2) return 0;

        decimal peakEquity = 0;
        double squaredDrawdownSum = 0;

        foreach (var equity in equityList)
        {
            if (equity > peakEquity) peakEquity = equity;
            if (peakEquity > 0)
            {
                var drawdownPercent = (double)((peakEquity - equity) / peakEquity * 100m);
                squaredDrawdownSum += drawdownPercent * drawdownPercent;
            }
        }

        return Math.Sqrt(squaredDrawdownSum / equityList.Count);
    }

    internal static double ComputeSortinoRatio(IEnumerable<decimal> returnsPercent, double riskFreeRatePercent = 0.0)
    {
        var returnList = returnsPercent.Select(x => (double)x).ToList();
        if (returnList.Count < 2) return 0;

        var meanReturn = returnList.Average();
        var downsideReturns = returnList.Where(r => r < riskFreeRatePercent).Select(r => r - riskFreeRatePercent).ToList();

        if (downsideReturns.Count == 0) return 99; // Infinite Sortino (no downside risk)

        var expectedDownsideVariance = downsideReturns.Sum(r => r * r) / returnList.Count;
        var downsideDeviation = Math.Sqrt(expectedDownsideVariance);

        if (downsideDeviation == 0) return 99;

        return (meanReturn - riskFreeRatePercent) / downsideDeviation;
    }
}
