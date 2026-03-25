using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class StrategyExpectancyServiceTests
{
    private readonly StrategyExpectancyService _service = new();

    [Fact]
    public void Evaluate_ShouldBlock_WhenBucketExpectancyIsNegative()
    {
        var trades = new[]
        {
            CreateTrade(-120m),
            CreateTrade(-110m),
            CreateTrade(-90m),
            CreateTrade(-80m),
            CreateTrade(35m)
        };

        var signal = CreateSignal();
        var decision = CreateDecision();

        var result = _service.Evaluate(trades, signal, decision);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("Expectancy guard blocked");
    }

    [Fact]
    public void Evaluate_ShouldReduceSize_WhenBucketEdgeIsSoft()
    {
        var trades = new[]
        {
            CreateTrade(60m),
            CreateTrade(50m),
            CreateTrade(-40m),
            CreateTrade(-35m),
            CreateTrade(-30m)
        };

        var signal = CreateSignal();
        var decision = CreateDecision();

        var result = _service.Evaluate(trades, signal, decision);

        result.Approved.Should().BeTrue();
        result.SizeMultiplier.Should().BeLessThan(1m);
    }

    private static Signal CreateSignal() => new()
    {
        Symbol = "THYAO",
        Timestamp = DateTime.UtcNow,
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending"
    };

    private static TradeDecision CreateDecision() => new()
    {
        ShouldTrade = true,
        Side = TradeSide.Buy,
        Confidence = 75,
        PositionSizePercent = 0.03m,
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending"
    };

    private static Trade CreateTrade(decimal pnl)
    {
        const decimal entry = 100m;

        return new Trade
        {
            SignalId = Guid.NewGuid(),
            Symbol = "THYAO",
            Side = TradeSide.Buy,
            EntryPrice = entry,
            ExitPrice = entry + pnl,
            Quantity = 1m,
            Status = TradeStatus.Executed,
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending",
            EntryTime = DateTime.UtcNow.AddHours(-4),
            ExitTime = DateTime.UtcNow.AddHours(-1)
        };
    }
}
