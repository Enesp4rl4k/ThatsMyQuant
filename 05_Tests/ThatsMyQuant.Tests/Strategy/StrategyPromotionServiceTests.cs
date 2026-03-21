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
        service.RecordBacktestState(CreateBacktestResult());
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
        result.PromotionStage.Should().Be("LiveProbe");
    }

    [Fact]
    public void Evaluate_ShouldBoostSize_WhenExactSymbolSessionBucketIsScaledLiveAndHealthy()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new StrategyPromotionService(store);
        service.RecordBacktestState(CreateBacktestResult());
        service.RecordPaperState(new[]
        {
            CreatePaperPosition(120m),
            CreatePaperPosition(110m),
            CreatePaperPosition(90m),
            CreatePaperPosition(100m),
            CreatePaperPosition(80m),
            CreatePaperPosition(85m),
            CreatePaperPosition(75m),
            CreatePaperPosition(95m)
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(28m, 0.04m, 110, edgeScore: 90m),
            CreateLiveTrade(24m, 0.03m, 105, edgeScore: 88m),
            CreateLiveTrade(22m, 0.05m, 120, edgeScore: 86m),
            CreateLiveTrade(20m, 0.04m, 115, edgeScore: 84m),
            CreateLiveTrade(18m, 0.03m, 118, edgeScore: 83m),
            CreateLiveTrade(17m, 0.04m, 112, edgeScore: 85m),
            CreateLiveTrade(16m, 0.04m, 108, edgeScore: 82m),
            CreateLiveTrade(15m, 0.05m, 110, edgeScore: 87m)
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeTrue();
        result.PromotionStage.Should().Be("ScaledLive");
        result.SizeMultiplier.Should().BeGreaterThan(1.05m);
        result.Reason.Should().Contain("exact symbol-session bucket");
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenPaperLiveDriftIsSevere()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new StrategyPromotionService(store);
        service.RecordBacktestState(CreateBacktestResult());
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
        result.Reason.Should().MatchRegex("(drift|High-edge outcomes)");
    }

    [Fact]
    public void Evaluate_ShouldBlock_HftBucket_WhenLatencyBudgetIsBlown()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new StrategyPromotionService(store);
        service.RecordBacktestState(CreateBacktestResult("BTC-USD", "HftMicroEdge", "Volatile"));
        service.RecordPaperState(new[]
        {
            CreatePaperPosition(80m, strategyTag: "HftMicroEdge", regimeTag: "Volatile"),
            CreatePaperPosition(85m, strategyTag: "HftMicroEdge", regimeTag: "Volatile"),
            CreatePaperPosition(75m, strategyTag: "HftMicroEdge", regimeTag: "Volatile"),
            CreatePaperPosition(90m, strategyTag: "HftMicroEdge", regimeTag: "Volatile"),
            CreatePaperPosition(78m, strategyTag: "HftMicroEdge", regimeTag: "Volatile"),
            CreatePaperPosition(82m, strategyTag: "HftMicroEdge", regimeTag: "Volatile"),
            CreatePaperPosition(70m, strategyTag: "HftMicroEdge", regimeTag: "Volatile"),
            CreatePaperPosition(88m, strategyTag: "HftMicroEdge", regimeTag: "Volatile")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(15m, 0.05m, 240, "HftMicroEdge", "Volatile"),
            CreateLiveTrade(12m, 0.04m, 235, "HftMicroEdge", "Volatile"),
            CreateLiveTrade(11m, 0.05m, 245, "HftMicroEdge", "Volatile"),
            CreateLiveTrade(10m, 0.05m, 255, "HftMicroEdge", "Volatile")
        };

        var signal = CreateSignal();
        signal.Symbol = "BTC-USD";
        signal.Metadata["Venue"] = "Binance";
        var decision = CreateDecision() with { StrategyTag = "HftMicroEdge", RegimeTag = "Volatile" };

        var result = service.Evaluate(signal, decision, liveTrades);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("latency");
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenLiveAverageEdgeScoreIsTooWeak()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new StrategyPromotionService(store);
        service.RecordBacktestState(CreateBacktestResult());
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
            CreateLiveTrade(30m, edgeScore: 52m),
            CreateLiveTrade(25m, edgeScore: 50m),
            CreateLiveTrade(20m, edgeScore: 54m),
            CreateLiveTrade(18m, edgeScore: 53m)
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("edge quality");
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenHighEdgeTradesStillLoseDespiteOkayOverallBucket()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new StrategyPromotionService(store);
        service.RecordBacktestState(CreateBacktestResult());
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
            CreateLiveTrade(-12m, edgeScore: 88m),
            CreateLiveTrade(-10m, edgeScore: 82m),
            CreateLiveTrade(-8m, edgeScore: 78m),
            CreateLiveTrade(-6m, edgeScore: 76m)
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("High-edge outcomes");
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenBacktestLadderGateIsMissing()
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

        var result = service.Evaluate(CreateSignal(), CreateDecision(), Array.Empty<Trade>());

        result.Approved.Should().BeFalse();
        result.PromotionStage.Should().Be("Backtest");
        result.Reason.Should().Contain("Backtest ladder gate");
    }

    private static Signal CreateSignal() => new()
    {
        Symbol = "THYAO",
        Timestamp = new DateTime(2026, 03, 21, 8, 30, 0, DateTimeKind.Utc),
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

    private static PaperPosition CreatePaperPosition(decimal pnl, string strategyTag = "TrendConfluence", string regimeTag = "Trending") => new()
    {
        Symbol = "THYAO",
        Side = "Buy",
        EntryPrice = 100m,
        CurrentPrice = 100m,
        Quantity = 1m,
        RealizedPnL = pnl,
        ExitTime = new DateTime(2026, 03, 21, 8, 45, 0, DateTimeKind.Utc),
        StrategyTag = strategyTag,
        RegimeTag = regimeTag
    };

    private static Trade CreateLiveTrade(
        decimal pnl,
        decimal slippagePercent = 0m,
        long executionLatencyMs = 0,
        string strategyTag = "TrendConfluence",
        string regimeTag = "Trending",
        decimal edgeScore = 75m) => new()
    {
        Symbol = "THYAO",
        Side = TradeSide.Buy,
        EntryPrice = 100m,
        ExitPrice = 100m + pnl,
        Quantity = 1m,
        Status = TradeStatus.Executed,
        StrategyTag = strategyTag,
        RegimeTag = regimeTag,
        EdgeScore = edgeScore,
        EdgeBand = edgeScore >= 85m ? "Full" : edgeScore >= 70m ? "Reduced" : edgeScore >= 55m ? "Probe" : "Reject",
        EntrySlippagePercent = slippagePercent,
        ExecutionLatencyMs = executionLatencyMs,
        ExitTime = new DateTime(2026, 03, 21, 8, 50, 0, DateTimeKind.Utc)
    };

    private static BacktestResult CreateBacktestResult(
        string symbol = "THYAO",
        string strategyTag = "TrendConfluence",
        string regimeTag = "Trending") => new()
    {
        Symbol = symbol,
        TotalTrades = 8,
        WinningTrades = 6,
        LosingTrades = 2,
        TotalPnL = 420m,
        ProfitFactor = 1.45,
        Trades = new List<BacktestTradeResult>
        {
            CreateBacktestTrade(120m, symbol, strategyTag, regimeTag, new DateTime(2026, 03, 21, 8, 0, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(90m, symbol, strategyTag, regimeTag, new DateTime(2026, 03, 21, 8, 5, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(80m, symbol, strategyTag, regimeTag, new DateTime(2026, 03, 21, 8, 10, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(75m, symbol, strategyTag, regimeTag, new DateTime(2026, 03, 21, 8, 15, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(65m, symbol, strategyTag, regimeTag, new DateTime(2026, 03, 21, 8, 20, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(55m, symbol, strategyTag, regimeTag, new DateTime(2026, 03, 21, 8, 25, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(-30m, symbol, strategyTag, regimeTag, new DateTime(2026, 03, 21, 8, 35, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(-35m, symbol, strategyTag, regimeTag, new DateTime(2026, 03, 21, 8, 40, 0, DateTimeKind.Utc))
        }
    };

    private static BacktestTradeResult CreateBacktestTrade(
        decimal pnl,
        string symbol,
        string strategyTag,
        string regimeTag,
        DateTime exitTime) => new()
    {
        EntryTime = exitTime.AddMinutes(-30),
        ExitTime = exitTime,
        Side = "Buy",
        StrategyTag = strategyTag,
        RegimeTag = regimeTag,
        ExecutionMode = "SIMULATED",
        PromotionStage = "Backtest",
        EntryPrice = 100m,
        ExitPrice = 100m + pnl,
        PnL = pnl,
        PnLPercent = pnl
    };

    private sealed class InMemoryStrategyPerformanceStore : IStrategyPerformanceStore
    {
        private StrategyPerformanceState _state = new();

        public StrategyPerformanceState Load() => _state;

        public void Save(StrategyPerformanceState state) => _state = state;
    }
}
