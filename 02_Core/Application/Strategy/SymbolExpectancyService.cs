using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

public sealed class SymbolExpectancyService
{
    public SymbolGuardResult Evaluate(IReadOnlyList<Trade> recentTrades, Signal signal)
    {
        var symbolTrades = FindBestSymbolBucket(recentTrades, signal);

        if (symbolTrades.Count < 4)
        {
            return new SymbolGuardResult(true, 1.00m, "Symbol guard: sample too small, no symbol penalty applied.");
        }

        var pnlValues = symbolTrades.Select(GetPnl).ToList();
        var expectedValue = pnlValues.Average();
        var grossProfit = pnlValues.Where(pnl => pnl > 0).Sum();
        var grossLoss = Math.Abs(pnlValues.Where(pnl => pnl < 0).Sum());
        var profitFactor = grossLoss <= 0 ? (grossProfit > 0 ? 99 : 0) : (double)(grossProfit / grossLoss);
        var winRate = pnlValues.Count(pnl => pnl > 0) / (double)pnlValues.Count;

        if (expectedValue <= 0 && profitFactor < 0.95)
        {
            return new SymbolGuardResult(
                false,
                0m,
                $"Symbol guard blocked {signal.Symbol}: EV {expectedValue:F2}, PF {profitFactor:F2}, WR {winRate:P0}.");
        }

        if (expectedValue <= 0 || profitFactor < 1.05 || winRate < 0.40)
        {
            return new SymbolGuardResult(
                true,
                0.55m,
                $"Symbol guard reduced {signal.Symbol}: EV {expectedValue:F2}, PF {profitFactor:F2}, WR {winRate:P0}.");
        }

        return new SymbolGuardResult(
            true,
            1.00m,
            $"Symbol guard confirmed {signal.Symbol}: EV {expectedValue:F2}, PF {profitFactor:F2}, WR {winRate:P0}.");
    }

    private static decimal GetPnl(Trade trade)
        => (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == TradeSide.Sell ? -1 : 1);

    private static List<Trade> FindBestSymbolBucket(IReadOnlyList<Trade> recentTrades, Signal signal)
    {
        var sessionTag = ResolveSessionBucket(signal.Timestamp);
        var closedTrades = recentTrades
            .Where(trade => trade.IsClosed &&
                            string.Equals(trade.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(trade => trade.ExitTime)
            .ToList();
        var sessionTrades = closedTrades
            .Where(trade => string.Equals(ResolveSessionBucket(trade.ExitTime ?? trade.EntryTime), sessionTag, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();

        if (sessionTrades.Count >= 4)
        {
            return sessionTrades;
        }

        return closedTrades.Take(20).ToList();
    }

    private static string ResolveSessionBucket(DateTime timestamp)
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
}

public readonly record struct SymbolGuardResult(bool Approved, decimal SizeMultiplier, string Reason);
