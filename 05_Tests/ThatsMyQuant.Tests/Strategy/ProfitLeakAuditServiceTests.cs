using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class ProfitLeakAuditServiceTests
{
    private readonly ProfitLeakAuditService _service = new();

    [Fact]
    public void Evaluate_ShouldReject_WhenExactBucketIsNetNegativeAfterCosts()
    {
        var signal = CreateSignal();
        var decision = CreateDecision(size: 0.04m);
        var trades = new[]
        {
            CreateTrade(-30m),
            CreateTrade(-24m),
            CreateTrade(-18m),
            CreateTrade(-8m)
        };

        var result = _service.Evaluate(signal, decision, decision, trades);

        result.Approved.Should().BeFalse();
        result.ExactBucket.SampleSize.Should().Be(4);
        result.ExactBucket.ExpectedValue.Should().BeLessThan(0m);
        result.Summary.Should().Contain("Exact bucket is net-negative");
    }

    [Fact]
    public void Evaluate_ShouldReject_WhenReducedSizeTradesAreNoise()
    {
        var signal = CreateSignal();
        var original = CreateDecision(size: 0.04m);
        var reduced = original with { PositionSizePercent = 0.02m };
        var trades = new[]
        {
            CreateTrade(-14m, positionSizePercent: 0.018m, finalAllocatorBias: 0.82m),
            CreateTrade(-12m, positionSizePercent: 0.017m, finalAllocatorBias: 0.85m),
            CreateTrade(-10m, positionSizePercent: 0.019m, finalAllocatorBias: 0.80m),
            CreateTrade(1m, positionSizePercent: 0.016m, finalAllocatorBias: 0.84m),
            CreateTrade(50m, positionSizePercent: 0.040m, finalAllocatorBias: 1.00m),
            CreateTrade(40m, positionSizePercent: 0.041m, finalAllocatorBias: 1.00m),
            CreateTrade(30m, positionSizePercent: 0.039m, finalAllocatorBias: 1.00m),
            CreateTrade(20m, positionSizePercent: 0.038m, finalAllocatorBias: 1.00m)
        };

        var result = _service.Evaluate(signal, original, reduced, trades);

        result.CurrentTradeIsReducedSize.Should().BeTrue();
        result.ReducedSizeBucket.SampleSize.Should().Be(4);
        result.Approved.Should().BeFalse();
        result.Summary.Should().Contain("Reduced-size trades are behaving like noise");
    }

    [Fact]
    public void Evaluate_ShouldDetect_WhenLossesClusterAroundDegradedExecution()
    {
        var signal = CreateSignal();
        var decision = CreateDecision(size: 0.04m);
        var trades = new[]
        {
            CreateTrade(-8m, slippagePercent: 0.22m, executionLatencyMs: 260, venueExecutionBias: 0.95m),
            CreateTrade(-6m, slippagePercent: 0.20m, executionLatencyMs: 240, venueExecutionBias: 0.96m),
            CreateTrade(-5m, slippagePercent: 0.21m, executionLatencyMs: 255, venueExecutionBias: 0.95m),
            CreateTrade(-4m, slippagePercent: 0.19m, executionLatencyMs: 235, venueExecutionBias: 0.97m),
            CreateTrade(30m, slippagePercent: 0.02m, executionLatencyMs: 50, venueExecutionBias: 1.01m),
            CreateTrade(26m, slippagePercent: 0.02m, executionLatencyMs: 45, venueExecutionBias: 1.02m),
            CreateTrade(24m, slippagePercent: 0.02m, executionLatencyMs: 48, venueExecutionBias: 1.01m),
            CreateTrade(22m, slippagePercent: 0.02m, executionLatencyMs: 46, venueExecutionBias: 1.01m)
        };

        var result = _service.Evaluate(signal, decision, decision, trades);

        result.LoserCluster.LosingTrades.Should().Be(4);
        result.LoserCluster.DegradedExecutionLossRate.Should().BeGreaterThanOrEqualTo(0.65);
    }

    private static Signal CreateSignal() => new()
    {
        Id = Guid.NewGuid(),
        Symbol = "BTC-USD",
        Side = TradeSide.Buy,
        Price = 100m,
        Timestamp = new DateTime(2026, 4, 22, 8, 0, 0, DateTimeKind.Utc),
        Metadata = new Dictionary<string, string> { ["Venue"] = "Binance" }
    };

    private static TradeDecision CreateDecision(decimal size) => new()
    {
        ShouldTrade = true,
        Side = TradeSide.Buy,
        Confidence = 78,
        Reasoning = "profit leak audit test",
        StopLoss = 96m,
        TakeProfit = 108m,
        PositionSizePercent = size,
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending",
        ExitPolicyTag = "TrendRunner"
    };

    private static Trade CreateTrade(
        decimal realizedPnl,
        decimal positionSizePercent = 0.04m,
        decimal finalAllocatorBias = 1.00m,
        decimal slippagePercent = 0.02m,
        long executionLatencyMs = 50,
        decimal venueExecutionBias = 1.00m)
    {
        var quantity = 1m;
        var entryPrice = 100m;
        var exitPrice = entryPrice + realizedPnl;

        return new Trade
        {
            Symbol = "BTC-USD",
            Side = TradeSide.Buy,
            EntryPrice = entryPrice,
            ExitPrice = exitPrice,
            Quantity = quantity,
            ClosedQuantity = quantity,
            AverageExitPrice = exitPrice,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Closed,
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending",
            ExitPolicyTag = "TrendRunner",
            PositionSizePercent = positionSizePercent,
            FinalAllocatorBias = finalAllocatorBias,
            EntrySlippagePercent = slippagePercent,
            ExecutionLatencyMs = executionLatencyMs,
            VenueExecutionBias = venueExecutionBias,
            EntryTime = new DateTime(2026, 4, 22, 7, 0, 0, DateTimeKind.Utc),
            ExitTime = new DateTime(2026, 4, 22, 8, 0, 0, DateTimeKind.Utc)
        };
    }
}
