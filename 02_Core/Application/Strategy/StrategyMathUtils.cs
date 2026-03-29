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
}
