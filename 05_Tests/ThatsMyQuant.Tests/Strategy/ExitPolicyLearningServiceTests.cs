using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class ExitPolicyLearningServiceTests
{
    [Fact]
    public async Task ApplyLearnedPolicy_ShouldPromoteBestExitPolicy_ForExactBucket()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new ExitPolicyLearningService(store);

        await service.RecordBacktestState(CreateBacktestResult("TrendRunner", new[] { -20m, 10m, -15m, 8m }));
        await service.RecordBacktestState(CreateBacktestResult("MeanReversion", new[] { 35m, 28m, 22m, 26m }));

        var signal = CreateSignal();
        var decision = CreateDecision();

        var updated = await service.ApplyLearnedPolicy(signal, decision, 100m, 2m);

        updated.ExitPolicyTag.Should().Be("MeanReversion");
        updated.MaxHoldingBars.Should().Be(8);
        updated.Reasoning.Should().Contain("Exit learning promoted MeanReversion");
    }

    [Fact]
    public async Task ApplyLearnedPolicy_ShouldLetRealOutcomesOutweighBacktestAndPaper()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new ExitPolicyLearningService(store);

        await service.RecordBacktestState(CreateBacktestResult("TrendRunner", new[] { 30m, 25m, 18m, 12m }));
        await service.RecordBacktestState(CreateBacktestResult("MeanReversion", new[] { 18m, 16m, 14m, 12m }));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition("MeanReversion", 25m),
            CreatePaperPosition("MeanReversion", 22m),
            CreatePaperPosition("MeanReversion", 18m),
            CreatePaperPosition("TrendRunner", 6m),
            CreatePaperPosition("TrendRunner", -3m),
            CreatePaperPosition("TrendRunner", 4m)
        });
        await service.RecordLiveState(new[]
        {
            CreateLiveTrade("TrendRunner", 28m),
            CreateLiveTrade("TrendRunner", 22m),
            CreateLiveTrade("TrendRunner", 20m),
            CreateLiveTrade("MeanReversion", -8m),
            CreateLiveTrade("MeanReversion", 4m),
            CreateLiveTrade("MeanReversion", -6m)
        });

        var updated = await service.ApplyLearnedPolicy(CreateSignal(), CreateDecision(), 100m, 2m);

        updated.ExitPolicyTag.Should().Be("TrendRunner");
    }

    [Fact]
    public async Task ApplyLiveSizingConfidence_ShouldBoostSize_WhenRealExitPolicyPerformsStrongly()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new ExitPolicyLearningService(store);

        await service.RecordLiveState(new[]
        {
            CreateLiveTrade("TrendRunner", 18m),
            CreateLiveTrade("TrendRunner", 14m),
            CreateLiveTrade("TrendRunner", 12m),
            CreateLiveTrade("TrendRunner", 11m)
        });

        var updated = await service.ApplyLiveSizingConfidence(CreateSignal(), CreateDecision());

        updated.PositionSizePercent.Should().BeGreaterThan(CreateDecision().PositionSizePercent);
        updated.Reasoning.Should().Contain("Exit sizing confidence");
    }

    [Fact]
    public async Task ApplyLiveSizingConfidence_ShouldReduceSize_WhenRealExitPolicyUnderperforms()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new ExitPolicyLearningService(store);

        await service.RecordLiveState(new[]
        {
            CreateLiveTrade("TrendRunner", -10m),
            CreateLiveTrade("TrendRunner", -8m),
            CreateLiveTrade("TrendRunner", 2m),
            CreateLiveTrade("TrendRunner", -6m)
        });

        var updated = await service.ApplyLiveSizingConfidence(CreateSignal(), CreateDecision());

        updated.PositionSizePercent.Should().BeLessThan(CreateDecision().PositionSizePercent);
        updated.Reasoning.Should().Contain("Exit sizing confidence");
    }

    [Fact]
    public async Task ApplyLearnedPolicy_ShouldDemotePolicy_WhenLiveDriftBreaksPaperWinner()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new ExitPolicyLearningService(store);

        await service.RecordPaperState(new[]
        {
            CreatePaperPosition("TrendRunner", 24m),
            CreatePaperPosition("TrendRunner", 22m),
            CreatePaperPosition("TrendRunner", 20m),
            CreatePaperPosition("TrendRunner", 18m),
            CreatePaperPosition("MeanReversion", 12m),
            CreatePaperPosition("MeanReversion", 10m),
            CreatePaperPosition("MeanReversion", 11m),
            CreatePaperPosition("MeanReversion", 9m)
        });
        await service.RecordLiveState(new[]
        {
            CreateLiveTrade("TrendRunner", -12m),
            CreateLiveTrade("TrendRunner", -9m),
            CreateLiveTrade("TrendRunner", 2m),
            CreateLiveTrade("TrendRunner", -6m),
            CreateLiveTrade("MeanReversion", 8m),
            CreateLiveTrade("MeanReversion", 7m),
            CreateLiveTrade("MeanReversion", 6m),
            CreateLiveTrade("MeanReversion", 5m)
        });

        var updated = await service.ApplyLearnedPolicy(CreateSignal(), CreateDecision(), 100m, 2m);

        updated.ExitPolicyTag.Should().Be("MeanReversion");
    }

    [Fact]
    public async Task ApplyLiveSizingConfidence_ShouldCutSize_WhenLiveVsPaperDriftIsSevere()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = new ExitPolicyLearningService(store);

        await service.RecordPaperState(new[]
        {
            CreatePaperPosition("TrendRunner", 20m),
            CreatePaperPosition("TrendRunner", 18m),
            CreatePaperPosition("TrendRunner", 17m),
            CreatePaperPosition("TrendRunner", 15m)
        });
        await service.RecordLiveState(new[]
        {
            CreateLiveTrade("TrendRunner", -10m),
            CreateLiveTrade("TrendRunner", -8m),
            CreateLiveTrade("TrendRunner", -6m),
            CreateLiveTrade("TrendRunner", 1m)
        });

        var updated = await service.ApplyLiveSizingConfidence(CreateSignal(), CreateDecision());

        updated.PositionSizePercent.Should().BeLessThan(CreateDecision().PositionSizePercent * 0.85m);
        updated.Reasoning.Should().Contain("drift");
    }

    private static Signal CreateSignal() => new()
    {
        Symbol = "THYAO",
        Price = 100m,
        Timestamp = new DateTime(2026, 3, 20, 8, 0, 0, DateTimeKind.Utc),
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending"
    };

    private static TradeDecision CreateDecision() => new()
    {
        ShouldTrade = true,
        Side = TradeSide.Buy,
        Confidence = 82,
        Reasoning = "Base trend decision",
        StopLoss = 97.5m,
        TakeProfit = 106.4m,
        PositionSizePercent = 0.03m,
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending",
        PartialTakeProfit = 103m,
        PartialExitFraction = 0.40m,
        ExitPolicyTag = "TrendRunner",
        BreakevenTriggerFraction = 0.55m,
        MaxHoldingBars = 18
    };

    private static BacktestResult CreateBacktestResult(string exitPolicyTag, decimal[] pnls) => new()
    {
        Symbol = "THYAO",
        TotalTrades = pnls.Length,
        WinningTrades = pnls.Count(value => value > 0),
        LosingTrades = pnls.Count(value => value <= 0),
        TotalPnL = pnls.Sum(),
        ProfitFactor = ComputeProfitFactor(pnls),
        Trades = pnls.Select((pnl, index) => new BacktestTradeResult
        {
            EntryTime = new DateTime(2026, 3, 20, 7, 30, 0, DateTimeKind.Utc).AddMinutes(index * 5),
            ExitTime = new DateTime(2026, 3, 20, 8, 0, 0, DateTimeKind.Utc).AddMinutes(index * 5),
            Side = TradeSide.Buy,
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending",
            ExitPolicyTag = exitPolicyTag,
            ExecutionMode = "SIMULATED",
            PromotionStage = "Backtest",
            EntryPrice = 100m,
            ExitPrice = 100m + pnl,
            PnL = pnl,
            PnLPercent = pnl
        }).ToList()
    };

    private static double ComputeProfitFactor(IEnumerable<decimal> pnls)
    {
        var pnlList = pnls.ToList();
        var grossProfit = pnlList.Where(value => value > 0).Sum();
        var grossLoss = Math.Abs(pnlList.Where(value => value < 0).Sum());
        return grossLoss == 0 ? 99 : (double)(grossProfit / grossLoss);
    }

    private static PaperPosition CreatePaperPosition(string exitPolicyTag, decimal realizedPnl) => new()
    {
        Symbol = "THYAO",
        Side = TradeSide.Buy,
        EntryPrice = 100m,
        CurrentPrice = 100m,
        Quantity = 1m,
        ExitPolicyTag = exitPolicyTag,
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending",
        ExecutionMode = "PAPER",
        RealizedPnL = realizedPnl,
        ExitTime = new DateTime(2026, 3, 20, 8, 15, 0, DateTimeKind.Utc)
    };

    private static Trade CreateLiveTrade(string exitPolicyTag, decimal pnl) => new()
    {
        Symbol = "THYAO",
        Side = TradeSide.Buy,
        EntryPrice = 100m,
        ExitPrice = 100m + pnl,
        Quantity = 1m,
        Status = TradeStatus.Executed,
        ExitPolicyTag = exitPolicyTag,
        StrategyTag = "TrendConfluence",
        RegimeTag = "Trending",
        ExecutionMode = "REAL",
        ExitTime = new DateTime(2026, 3, 20, 8, 20, 0, DateTimeKind.Utc)
    };

    private sealed class InMemoryStrategyPerformanceStore : IStrategyPerformanceStore
    {
        private StrategyPerformanceState _state = new();

        public Task<StrategyPerformanceState> LoadAsync() => Task.FromResult(_state);

        public Task SaveAsync(StrategyPerformanceState state) { _state = state; return Task.CompletedTask; }
    }
}
