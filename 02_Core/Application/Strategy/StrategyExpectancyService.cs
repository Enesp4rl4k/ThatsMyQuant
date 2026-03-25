using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public sealed class StrategyExpectancyService
{
    public ExpectancyGuardResult Evaluate(
        IReadOnlyList<Trade> recentTrades,
        Signal signal,
        TradeDecision decision)
    {
        var closedTrades = recentTrades
            .Where(trade => trade.IsClosed)
            .ToList();

        var bucketTrades = closedTrades
            .Where(trade =>
                string.Equals(trade.StrategyTag, decision.StrategyTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(trade.RegimeTag, decision.RegimeTag, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(trade => trade.ExitTime)
            .Take(30)
            .ToList();

        if (bucketTrades.Count < 4)
        {
            return new ExpectancyGuardResult(
                true,
                1.00m,
                "Expectancy guard: insufficient closed-trade sample, trade at normal size.",
                bucketTrades.Count,
                0,
                0);
        }

        var expectedValue = ComputeExpectedValue(bucketTrades);
        var profitFactor = ComputeProfitFactor(bucketTrades);
        var winRate = bucketTrades.Count(trade => ComputePnl(trade) > 0) / (double)bucketTrades.Count;

        var currentSession = ResolveSessionBucket(signal.Timestamp);
        var sessionTrades = bucketTrades
            .Where(trade => trade.ExitTime.HasValue &&
                            string.Equals(ResolveSessionBucket(trade.ExitTime.Value), currentSession, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sessionExpectedValue = sessionTrades.Count >= 3 ? ComputeExpectedValue(sessionTrades) : expectedValue;
        var sessionProfitFactor = sessionTrades.Count >= 3 ? ComputeProfitFactor(sessionTrades) : profitFactor;

        if (expectedValue <= 0 && profitFactor < 0.95)
        {
            return new ExpectancyGuardResult(
                false,
                0m,
                $"Expectancy guard blocked {decision.StrategyTag}/{decision.RegimeTag}: EV {expectedValue:F2}, PF {profitFactor:F2}, WR {winRate:P0}.",
                bucketTrades.Count,
                expectedValue,
                profitFactor);
        }

        if (sessionTrades.Count >= 3 && sessionExpectedValue <= 0 && sessionProfitFactor < 1.00)
        {
            return new ExpectancyGuardResult(
                false,
                0m,
                $"Session expectancy blocked {currentSession}: EV {sessionExpectedValue:F2}, PF {sessionProfitFactor:F2}.",
                sessionTrades.Count,
                sessionExpectedValue,
                sessionProfitFactor);
        }

        if (expectedValue <= 0 || profitFactor < 1.10 || winRate < 0.45)
        {
            return new ExpectancyGuardResult(
                true,
                0.60m,
                $"Expectancy guard reduced size: EV {expectedValue:F2}, PF {profitFactor:F2}, WR {winRate:P0}.",
                bucketTrades.Count,
                expectedValue,
                profitFactor);
        }

        if (sessionTrades.Count >= 3 && (sessionExpectedValue <= 0 || sessionProfitFactor < 1.10))
        {
            return new ExpectancyGuardResult(
                true,
                0.75m,
                $"Session edge is soft in {currentSession}: EV {sessionExpectedValue:F2}, PF {sessionProfitFactor:F2}.",
                sessionTrades.Count,
                sessionExpectedValue,
                sessionProfitFactor);
        }

        return new ExpectancyGuardResult(
            true,
            1.00m,
            $"Expectancy confirmed: EV {expectedValue:F2}, PF {profitFactor:F2}, WR {winRate:P0}.",
            bucketTrades.Count,
            expectedValue,
            profitFactor);
    }

    private static decimal ComputeExpectedValue(IEnumerable<Trade> trades)
    {
        var pnlValues = trades.Select(ComputePnl).ToList();
        if (pnlValues.Count == 0)
        {
            return 0m;
        }

        var wins = pnlValues.Where(pnl => pnl > 0).ToList();
        var losses = pnlValues.Where(pnl => pnl < 0).ToList();
        var winRate = pnlValues.Count(pnl => pnl > 0) / (decimal)pnlValues.Count;
        var avgWin = wins.Count > 0 ? wins.Average() : 0m;
        var avgLoss = losses.Count > 0 ? losses.Average() : 0m;

        return (winRate * avgWin) + ((1m - winRate) * avgLoss);
    }

    private static double ComputeProfitFactor(IEnumerable<Trade> trades)
    {
        var pnlValues = trades.Select(ComputePnl).ToList();
        var grossProfit = pnlValues.Where(pnl => pnl > 0).Sum();
        var grossLoss = Math.Abs(pnlValues.Where(pnl => pnl < 0).Sum());

        if (grossLoss <= 0)
        {
            return grossProfit > 0 ? 99 : 0;
        }

        return (double)(grossProfit / grossLoss);
    }

    private static decimal ComputePnl(Trade trade)
        => (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == Contracts.Enums.TradeSide.Sell ? -1 : 1);

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

public readonly record struct ExpectancyGuardResult(
    bool Approved,
    decimal SizeMultiplier,
    string Reason,
    int SampleSize,
    decimal ExpectedValue,
    double ProfitFactor);
