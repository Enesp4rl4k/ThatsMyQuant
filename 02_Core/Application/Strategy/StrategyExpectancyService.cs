using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

public sealed class StrategyExpectancyService
{
    private readonly StrategyLearningOptions _opts;

    public StrategyExpectancyService(IOptions<StrategyLearningOptions>? learningOptions = null)
    {
        _opts = learningOptions?.Value ?? new StrategyLearningOptions();
    }

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
            .Take(_opts.TradeLookback)
            .ToList();

        if (bucketTrades.Count < _opts.MinSample)
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

        var sessionExpectedValue = sessionTrades.Count >= _opts.SessionMinTrades ? ComputeExpectedValue(sessionTrades) : expectedValue;
        var sessionProfitFactor = sessionTrades.Count >= _opts.SessionMinTrades ? ComputeProfitFactor(sessionTrades) : profitFactor;

        if (expectedValue <= 0 && profitFactor < _opts.BlockProfitFactorThreshold)
        {
            return new ExpectancyGuardResult(
                false,
                0m,
                $"Expectancy guard blocked {decision.StrategyTag}/{decision.RegimeTag}: EV {expectedValue:F2}, PF {profitFactor:F2}, WR {winRate:P0}.",
                bucketTrades.Count,
                expectedValue,
                profitFactor);
        }

        if (sessionTrades.Count >= _opts.SessionMinTrades && sessionExpectedValue <= 0 && sessionProfitFactor < _opts.BlockSessionProfitFactorThreshold)
        {
            return new ExpectancyGuardResult(
                false,
                0m,
                $"Session expectancy blocked {currentSession}: EV {sessionExpectedValue:F2}, PF {sessionProfitFactor:F2}.",
                sessionTrades.Count,
                sessionExpectedValue,
                sessionProfitFactor);
        }

        if (expectedValue <= 0 || profitFactor < _opts.ReduceProfitFactorThreshold || winRate < _opts.ReduceWinRateThreshold)
        {
            return new ExpectancyGuardResult(
                true,
                _opts.ReduceSizeMultiplier,
                $"Expectancy guard reduced size: EV {expectedValue:F2}, PF {profitFactor:F2}, WR {winRate:P0}.",
                bucketTrades.Count,
                expectedValue,
                profitFactor);
        }

        if (sessionTrades.Count >= _opts.SessionMinTrades && (sessionExpectedValue <= 0 || sessionProfitFactor < _opts.ReduceProfitFactorThreshold))
        {
            return new ExpectancyGuardResult(
                true,
                _opts.SessionSoftMultiplier,
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

    private decimal ComputeExpectedValue(IEnumerable<Trade> trades)
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

    private double ComputeProfitFactor(IEnumerable<Trade> trades)
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

    private decimal ComputePnl(Trade trade)
    {
        var direction = trade.Side == Contracts.Enums.TradeSide.Sell ? -1m : 1m;
        var grossPnl = (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * direction;
        var feeRate = _opts.ComputePnlFeeRate; // 5 bps per side, 10 bps round trip
        var fees = (trade.EntryPrice * trade.Quantity + trade.ExitPrice * trade.Quantity) * feeRate;
        var slippageCost = trade.EntryPrice * trade.Quantity * (trade.EntrySlippagePercent / 100m);
        return grossPnl - fees - slippageCost;
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

public readonly record struct ExpectancyGuardResult(
    bool Approved,
    decimal SizeMultiplier,
    string Reason,
    int SampleSize,
    decimal ExpectedValue,
    double ProfitFactor);
