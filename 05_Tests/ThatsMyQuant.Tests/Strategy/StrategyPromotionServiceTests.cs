using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.ValueObjects;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class StrategyPromotionServiceTests
{
    [Fact]
    public void Evaluate_ShouldReduceSize_WhenPaperReadyButLiveSampleIsThin()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new StrategyPromotionService(store);
        service.RecordPaperState(new[]
        {
            CreatePaperPosition(120m),
            CreatePaperPosition(110m),
            CreatePaperPosition(90m),
            CreatePaperPosition(100m),
            CreatePaperPosition(80m),
            CreatePaperPosition(85m),
            CreatePaperPosition(-20m),
            CreatePaperPosition(95m)
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(50m),
            CreateLiveTrade(40m),
            CreateLiveTrade(-10m)
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeTrue();
        result.SizeMultiplier.Should().BeLessThan(1m);
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenPaperLiveDriftIsSevere()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new StrategyPromotionService(store);
        service.RecordPaperState(new[]
        {
            CreatePaperPosition(120m),
            CreatePaperPosition(90m),
            CreatePaperPosition(95m),
            CreatePaperPosition(85m),
            CreatePaperPosition(100m),
            CreatePaperPosition(88m),
            CreatePaperPosition(-10m),
            CreatePaperPosition(92m)
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(-120m, 0.45m, 260),
            CreateLiveTrade(-90m, 0.42m, 245),
            CreateLiveTrade(-80m, 0.40m, 240),
            CreateLiveTrade(-70m, 0.38m, 250),
            CreateLiveTrade(10m, 0.30m, 215),
            CreateLiveTrade(-60m, 0.35m, 230)
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("drift");
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
        Confidence = 80,
        PositionSizePercent = 0.03m,
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending"
    };

    private static PaperPosition CreatePaperPosition(decimal pnl) => new()
    {
        Symbol = "THYAO",
        Side = "Buy",
        EntryPrice = 100m,
        CurrentPrice = 100m,
        Quantity = 1m,
        RealizedPnL = pnl,
        ExitTime = DateTime.UtcNow.AddHours(-1),
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending"
    };

    private static Trade CreateLiveTrade(decimal pnl, decimal slippagePercent = 0m, long executionLatencyMs = 0) => new()
    {
        Symbol = "THYAO",
        Side = TradeSide.Buy,
        EntryPrice = 100m,
        ExitPrice = 100m + pnl,
        Quantity = 1m,
        Status = TradeStatus.Executed,
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending",
        EntrySlippagePercent = slippagePercent,
        ExecutionLatencyMs = executionLatencyMs,
        ExitTime = DateTime.UtcNow.AddHours(-1)
    };

    private sealed class InMemoryStrategyPerformanceStore : IStrategyPerformanceStore
    {
        private StrategyPerformanceState _state = new();

        public StrategyPerformanceState Load() => _state;

        public void Save(StrategyPerformanceState state) => _state = state;
    }
}
