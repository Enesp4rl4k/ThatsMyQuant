using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class StrategyPromotionServiceTests
{
    [Fact]
    public async Task Evaluate_ShouldBlock_WhenBacktestThresholdsAreRaisedViaConfiguration()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var config = BuildPromotionConfig(
            backtestMinTrades: 12,
            backtestMinProfitFactor: 2,
            backtestMinWinRate: 1,
            paperMinTrades: 8,
            paperMinProfitFactor: 1,
            paperMinWinRate: 0);
        var service = new StrategyPromotionService(store, configuration: config);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
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

    [Fact]
    public async Task Evaluate_ShouldBlock_WhenPaperThresholdsAreRaisedViaConfiguration()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var config = BuildPromotionConfig(
            backtestMinTrades: 1,
            backtestMinProfitFactor: 0,
            backtestMinWinRate: 0,
            paperMinTrades: 12,
            paperMinProfitFactor: 2,
            paperMinWinRate: 1);
        var service = new StrategyPromotionService(store, configuration: config);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
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
        result.PromotionStage.Should().Be("Paper");
        result.Reason.Should().Contain("Paper ladder gate");
    }

    [Fact]
    public async Task Evaluate_ShouldReduceSize_WhenPaperReadyButLiveSampleIsThin()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
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
    public async Task RecordBacktestState_ShouldPersistPortfolioValidationSummary_WhenMultiSymbolBatchClearsGate()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Promotion:Backtest:MinTrades"] = "6",
                ["Promotion:Backtest:MinProfitFactor"] = "1.15",
                ["Promotion:Backtest:MinWinRate"] = "0.42",
                ["Backtest:QualityGate:MinPortfolioReadySymbols"] = "4",
                ["Backtest:QualityGate:ShortMinSharpe"] = "0.10",
                ["Backtest:QualityGate:LongMinSharpe"] = "0.10",
                ["Backtest:QualityGate:MaxDrawdownPercent"] = "25"
            })
            .Build();
        var service = new StrategyPromotionService(store, configuration: configuration);

        await service.RecordBacktestState(CreateBacktestResult(symbol: "BTC-USD"));
        await service.RecordBacktestState(CreateBacktestResult(symbol: "ETH-USD"));
        await service.RecordBacktestState(CreateBacktestResult(symbol: "SOL-USD"));
        await service.RecordBacktestState(CreateBacktestResult(symbol: "AVAX-USD"));

        var state = service.GetState();

        state.PortfolioValidationReady.Should().BeTrue();
        state.PortfolioReadySymbols.Should().Be(4);
        state.PortfolioRequiredSymbols.Should().Be(4);
        state.PortfolioValidationSummary.Should().Contain("4/4 symbols");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldBoost_WhenExactBucketAndLiveExitPolicyAreHealthy()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(112m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(104m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(98m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(94m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(89m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(86m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(82m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(79m, exitPolicyTag: "TrendRunner")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(112m, 0.04m, 110, edgeScore: 90m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(104m, 0.03m, 105, edgeScore: 88m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(98m, 0.05m, 120, edgeScore: 86m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(94m, 0.04m, 115, edgeScore: 84m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(89m, 0.03m, 118, edgeScore: 83m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(86m, 0.04m, 112, edgeScore: 85m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(82m, 0.04m, 108, edgeScore: 82m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(79m, 0.05m, 110, edgeScore: 87m, exitPolicyTag: "TrendRunner")
        };

        var result = service.EvaluateFinalAllocatorBias(CreateSignal(), CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeGreaterThan(1.0m);
        result.Scope.Should().Contain("exact symbol-session bucket");
        result.Reason.Should().Contain("Exit overlay");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldTrim_WhenLiveExitPolicyDriftsFromPaper()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(24m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(22m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(20m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(18m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(16m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(14m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(12m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(10m, exitPolicyTag: "TrendRunner")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(-14m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 88m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(-12m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 84m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(-10m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 82m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(2m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 80m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(-8m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 79m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(1m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 78m, exitPolicyTag: "TrendRunner")
        };

        var result = service.EvaluateFinalAllocatorBias(CreateSignal(), CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeLessThan(1.0m);
        result.Reason.Should().Contain("drifting live vs paper");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldIncludeExactBucketLearning_WhenExactBucketIsHealthy()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(112m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(104m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(98m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(94m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(89m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(86m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(82m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(79m, exitPolicyTag: "TrendRunner")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(112m, 0.04m, 110, edgeScore: 90m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(104m, 0.03m, 105, edgeScore: 88m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(98m, 0.05m, 120, edgeScore: 86m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(94m, 0.04m, 115, edgeScore: 84m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(89m, 0.03m, 118, edgeScore: 83m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(86m, 0.04m, 112, edgeScore: 85m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(82m, 0.04m, 108, edgeScore: 82m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(79m, 0.05m, 110, edgeScore: 87m, exitPolicyTag: "TrendRunner")
        };

        var result = service.EvaluateFinalAllocatorBias(CreateSignal(), CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeGreaterThan(1.0m);
        result.Reason.Should().Contain("Exact-bucket learning");
        result.Reason.Should().Contain("exact symbol-session bucket");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldLearnFromSuccessfulBoostedAllocations()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(112m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(104m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(98m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(94m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(89m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(86m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(82m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(79m, exitPolicyTag: "TrendRunner")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(42m, edgeScore: 90m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.08m),
            CreateLiveTrade(38m, edgeScore: 88m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.07m),
            CreateLiveTrade(34m, edgeScore: 86m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.09m),
            CreateLiveTrade(30m, edgeScore: 84m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.06m),
            CreateLiveTrade(26m, edgeScore: 82m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.08m),
            CreateLiveTrade(22m, edgeScore: 80m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.05m),
            CreateLiveTrade(18m, edgeScore: 78m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.04m),
            CreateLiveTrade(16m, edgeScore: 76m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.03m)
        };

        var result = service.EvaluateFinalAllocatorBias(CreateSignal(), CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeGreaterThan(1.05m);
        result.Reason.Should().Contain("Allocation learning");
        result.Reason.Should().Contain("boosted allocations are working");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldCutBackAfterBoostedAllocationsUnderperform()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(112m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(104m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(98m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(94m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(89m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(86m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(82m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(79m, exitPolicyTag: "TrendRunner")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(-18m, edgeScore: 90m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.08m),
            CreateLiveTrade(-14m, edgeScore: 88m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.07m),
            CreateLiveTrade(-10m, edgeScore: 86m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.09m),
            CreateLiveTrade(2m, edgeScore: 84m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.06m),
            CreateLiveTrade(-8m, edgeScore: 82m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.05m),
            CreateLiveTrade(1m, edgeScore: 80m, exitPolicyTag: "TrendRunner", finalAllocatorBias: 1.04m)
        };

        var result = service.EvaluateFinalAllocatorBias(CreateSignal(), CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeLessThan(1.0m);
        result.Reason.Should().Contain("Allocation learning");
        result.Reason.Should().Contain("boosted allocations are underperforming");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldBoost_WhenVenueExecutionIsClean()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(symbol: "EURUSD=", exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(18m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(16m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(15m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(14m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(13m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(12m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(11m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(10m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(18m, slippagePercent: 0.01m, executionLatencyMs: 32, edgeScore: 88m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreateLiveTrade(16m, slippagePercent: 0.01m, executionLatencyMs: 35, edgeScore: 86m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreateLiveTrade(14m, slippagePercent: 0.01m, executionLatencyMs: 40, edgeScore: 84m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreateLiveTrade(12m, slippagePercent: 0.01m, executionLatencyMs: 38, edgeScore: 82m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=")
        };

        var signal = CreateSignal();
        signal.Symbol = "EURUSD=";
        signal.Metadata["Venue"] = "Oanda";

        var result = service.EvaluateFinalAllocatorBias(signal, CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeGreaterThan(1.0m);
        result.Reason.Should().Contain("Venue overlay");
        result.Reason.Should().Contain("execution is clean live");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldTrim_WhenVenueExecutionIsDegraded()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(symbol: "BTC-USD", strategyTag: "TrendConfluence", regimeTag: "Trending", exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(8m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(7m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(6m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(5m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(4m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(4m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(3m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(2m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(5m, slippagePercent: 0.20m, executionLatencyMs: 225, edgeScore: 68m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreateLiveTrade(4m, slippagePercent: 0.19m, executionLatencyMs: 210, edgeScore: 66m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreateLiveTrade(3m, slippagePercent: 0.18m, executionLatencyMs: 205, edgeScore: 64m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreateLiveTrade(2m, slippagePercent: 0.17m, executionLatencyMs: 215, edgeScore: 62m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD")
        };

        var signal = CreateSignal();
        signal.Symbol = "BTC-USD";
        signal.Metadata["Venue"] = "Binance";

        var result = service.EvaluateFinalAllocatorBias(signal, CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeLessThan(1.0m);
        result.Reason.Should().Contain("Venue overlay");
        result.Reason.Should().Contain("execution is degraded");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldLearnFromSuccessfulVenueBoosts()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(symbol: "EURUSD=", exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(18m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(16m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(15m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(14m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(13m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(12m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(11m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(10m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(14m, slippagePercent: 0.01m, executionLatencyMs: 30, edgeScore: 86m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.03m),
            CreateLiveTrade(12m, slippagePercent: 0.01m, executionLatencyMs: 28, edgeScore: 84m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.04m),
            CreateLiveTrade(10m, slippagePercent: 0.01m, executionLatencyMs: 32, edgeScore: 82m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.02m),
            CreateLiveTrade(8m, slippagePercent: 0.01m, executionLatencyMs: 34, edgeScore: 80m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.03m)
        };

        var signal = CreateSignal();
        signal.Symbol = "EURUSD=";
        signal.Metadata["Venue"] = "Oanda";

        var result = service.EvaluateFinalAllocatorBias(signal, CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeGreaterThan(1.02m);
        result.Reason.Should().Contain("Venue learning");
        result.Reason.Should().Contain("boosted execution outcomes are working");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldCutBackAfterVenueBoostsUnderperform()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(symbol: "BTC-USD", exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(20m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(18m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(17m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(16m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(15m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(14m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(13m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD"),
            CreatePaperPosition(12m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(-14m, slippagePercent: 0.18m, executionLatencyMs: 225, edgeScore: 82m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD", venueExecutionBias: 1.03m),
            CreateLiveTrade(-12m, slippagePercent: 0.19m, executionLatencyMs: 230, edgeScore: 80m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD", venueExecutionBias: 1.04m),
            CreateLiveTrade(-10m, slippagePercent: 0.17m, executionLatencyMs: 220, edgeScore: 78m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD", venueExecutionBias: 1.02m),
            CreateLiveTrade(2m, slippagePercent: 0.18m, executionLatencyMs: 228, edgeScore: 76m, exitPolicyTag: "TrendRunner", symbol: "BTC-USD", venueExecutionBias: 1.03m)
        };

        var signal = CreateSignal();
        signal.Symbol = "BTC-USD";
        signal.Metadata["Venue"] = "Binance";

        var result = service.EvaluateFinalAllocatorBias(signal, CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeLessThan(1.0m);
        result.Reason.Should().Contain("Venue learning");
        result.Reason.Should().Contain("boosted execution outcomes are underperforming");
    }

    [Fact]
    public async Task EvaluateFinalAllocatorBias_ShouldApplyConcordanceOverlay_WhenExactBucketAndVenueBothSupport()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(symbol: "EURUSD=", exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(18m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(16m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(15m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(14m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(13m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(12m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(11m, exitPolicyTag: "TrendRunner", symbol: "EURUSD="),
            CreatePaperPosition(10m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(18m, slippagePercent: 0.01m, executionLatencyMs: 30, edgeScore: 90m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.03m),
            CreateLiveTrade(16m, slippagePercent: 0.01m, executionLatencyMs: 32, edgeScore: 88m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.04m),
            CreateLiveTrade(14m, slippagePercent: 0.01m, executionLatencyMs: 34, edgeScore: 86m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.03m),
            CreateLiveTrade(12m, slippagePercent: 0.01m, executionLatencyMs: 31, edgeScore: 84m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.02m),
            CreateLiveTrade(11m, slippagePercent: 0.01m, executionLatencyMs: 33, edgeScore: 83m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.03m),
            CreateLiveTrade(10m, slippagePercent: 0.01m, executionLatencyMs: 35, edgeScore: 82m, exitPolicyTag: "TrendRunner", symbol: "EURUSD=", venueExecutionBias: 1.02m)
        };

        var signal = CreateSignal();
        signal.Symbol = "EURUSD=";
        signal.Metadata["Venue"] = "Oanda";

        var result = service.EvaluateFinalAllocatorBias(signal, CreateDecision(), liveTrades);

        result.SizeMultiplier.Should().BeGreaterThan(1.05m);
        result.Reason.Should().Contain("Concordance overlay");
        result.Reason.Should().Contain("aligned exact-bucket edge and venue execution support");
    }

    [Fact]
    public async Task Evaluate_ShouldBoostSize_WhenExactSymbolSessionBucketIsScaledLiveAndHealthy()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(112m),
            CreatePaperPosition(104m),
            CreatePaperPosition(98m),
            CreatePaperPosition(94m),
            CreatePaperPosition(89m),
            CreatePaperPosition(86m),
            CreatePaperPosition(82m),
            CreatePaperPosition(79m)
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(112m, 0.04m, 110, edgeScore: 90m),
            CreateLiveTrade(104m, 0.03m, 105, edgeScore: 88m),
            CreateLiveTrade(98m, 0.05m, 120, edgeScore: 86m),
            CreateLiveTrade(94m, 0.04m, 115, edgeScore: 84m),
            CreateLiveTrade(89m, 0.03m, 118, edgeScore: 83m),
            CreateLiveTrade(86m, 0.04m, 112, edgeScore: 85m),
            CreateLiveTrade(82m, 0.04m, 108, edgeScore: 82m),
            CreateLiveTrade(79m, 0.05m, 110, edgeScore: 87m)
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeTrue();
        result.PromotionStage.Should().BeOneOf("ScaledLive", "LiveProbe");
        result.SizeMultiplier.Should().BeGreaterThan(0.20m);
        result.Reason.Should().Contain("exact symbol-session bucket");
    }

    [Fact]
    public async Task Evaluate_ShouldDemote_WhenExactBucketLearningShowsExactOutcomesAreSoft()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(8m),
            CreatePaperPosition(7m),
            CreatePaperPosition(6m),
            CreatePaperPosition(5m),
            CreatePaperPosition(4m),
            CreatePaperPosition(4m),
            CreatePaperPosition(3m),
            CreatePaperPosition(2m)
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(3m, edgeScore: 68m),
            CreateLiveTrade(2m, edgeScore: 66m),
            CreateLiveTrade(-1m, edgeScore: 64m),
            CreateLiveTrade(1m, edgeScore: 62m)
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeTrue();
        result.PromotionStage.Should().Be("LiveProbe");
        result.SizeMultiplier.Should().BeLessThanOrEqualTo(0.25m);
        result.Reason.Should().MatchRegex("(Exact bucket learning|edge quality)");
    }

    [Fact]
    public async Task Evaluate_ShouldBlock_WhenPaperLiveDriftIsSevere()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
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
        result.Reason.Should().MatchRegex("(drift|High-edge outcomes|Exact bucket learning|Concordance)");
    }

    [Fact]
    public async Task Evaluate_ShouldBlock_WhenExitPolicyDriftIsSevere()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(24m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(22m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(20m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(18m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(16m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(14m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(12m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(10m, exitPolicyTag: "TrendRunner")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(-14m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 58m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(-12m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 57m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(-10m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 56m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(2m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 55m, exitPolicyTag: "TrendRunner")
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.PromotionStage.Should().BeOneOf("Paper", "LiveProbe");
        result.Reason.Should().MatchRegex("(Exit-policy drift|edge quality|High-edge outcomes|Exact bucket learning)");
    }

    [Fact]
    public async Task Evaluate_ShouldReduceSize_WhenExitPolicyDriftIsElevated()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(exitPolicyTag: "TrendRunner"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(16m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(15m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(14m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(13m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(12m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(11m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(10m, exitPolicyTag: "TrendRunner"),
            CreatePaperPosition(9m, exitPolicyTag: "TrendRunner")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(12m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 58m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(8m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 57m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(4m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 56m, exitPolicyTag: "TrendRunner"),
            CreateLiveTrade(2m, strategyTag: "TrendConfluence", regimeTag: "Trending", edgeScore: 55m, exitPolicyTag: "TrendRunner")
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeTrue();
        result.PromotionStage.Should().Be("LiveProbe");
        result.SizeMultiplier.Should().BeLessThanOrEqualTo(0.25m);
        result.Reason.Should().MatchRegex("(Exit-policy drift caution|Paper/live drift caution|edge quality)");
    }

    [Fact]
    public async Task Evaluate_ShouldBlock_HftBucket_WhenLatencyBudgetIsBlown()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult("BTC-USD", "HftMicroEdge", "Volatile"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(80m, strategyTag: "HftMicroEdge", regimeTag: "Volatile", symbol: "BTC-USD"),
            CreatePaperPosition(85m, strategyTag: "HftMicroEdge", regimeTag: "Volatile", symbol: "BTC-USD"),
            CreatePaperPosition(75m, strategyTag: "HftMicroEdge", regimeTag: "Volatile", symbol: "BTC-USD"),
            CreatePaperPosition(90m, strategyTag: "HftMicroEdge", regimeTag: "Volatile", symbol: "BTC-USD"),
            CreatePaperPosition(78m, strategyTag: "HftMicroEdge", regimeTag: "Volatile", symbol: "BTC-USD"),
            CreatePaperPosition(82m, strategyTag: "HftMicroEdge", regimeTag: "Volatile", symbol: "BTC-USD"),
            CreatePaperPosition(70m, strategyTag: "HftMicroEdge", regimeTag: "Volatile", symbol: "BTC-USD"),
            CreatePaperPosition(88m, strategyTag: "HftMicroEdge", regimeTag: "Volatile", symbol: "BTC-USD")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(15m, 0.05m, 240, "HftMicroEdge", "Volatile", symbol: "BTC-USD"),
            CreateLiveTrade(12m, 0.04m, 235, "HftMicroEdge", "Volatile", symbol: "BTC-USD"),
            CreateLiveTrade(11m, 0.05m, 245, "HftMicroEdge", "Volatile", symbol: "BTC-USD"),
            CreateLiveTrade(10m, 0.05m, 255, "HftMicroEdge", "Volatile", symbol: "BTC-USD")
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
    public async Task Evaluate_ShouldBlock_WhenLiveAverageEdgeScoreIsTooWeak()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
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
    public async Task Evaluate_ShouldDemoteToPaper_WhenCriticalMlDriftPersists()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
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
            CreateLiveTrade(12m, edgeScore: 84m, modelVersion: "PredictiveAlphaModel@20260328063000", featureDriftDetected: true, featureDriftCritical: true, featureDriftMaxZScore: 5.2m),
            CreateLiveTrade(10m, edgeScore: 82m, modelVersion: "PredictiveAlphaModel@20260328063000", featureDriftDetected: true, featureDriftCritical: true, featureDriftMaxZScore: 4.9m),
            CreateLiveTrade(8m, edgeScore: 80m, modelVersion: "PredictiveAlphaModel@20260328063000"),
            CreateLiveTrade(6m, edgeScore: 78m, modelVersion: "PredictiveAlphaModel@20260328063000")
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeFalse();
        result.PromotionStage.Should().Be("Paper");
        result.Reason.Should().Contain("ML drift is blocking promotion");
        result.Reason.Should().Contain("PredictiveAlphaModel@20260328063000");
    }

    [Fact]
    public async Task Evaluate_ShouldHoldInLiveProbe_WhenMlDriftKeepsTrimmingSize()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
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
            CreateLiveTrade(12m, edgeScore: 84m, modelVersion: "PredictiveAlphaModel@20260328063000", featureDriftDetected: true, featureDriftCritical: false, featureDriftMaxZScore: 4.1m),
            CreateLiveTrade(10m, edgeScore: 82m, modelVersion: "PredictiveAlphaModel@20260328063000", featureDriftDetected: true, featureDriftCritical: false, featureDriftMaxZScore: 4.0m),
            CreateLiveTrade(8m, edgeScore: 80m, modelVersion: "PredictiveAlphaModel@20260328063000", featureDriftDetected: true, featureDriftCritical: false, featureDriftMaxZScore: 3.8m),
            CreateLiveTrade(6m, edgeScore: 78m, modelVersion: "PredictiveAlphaModel@20260328063000")
        };

        var result = service.Evaluate(CreateSignal(), CreateDecision(), liveTrades);

        result.Approved.Should().BeTrue();
        result.PromotionStage.Should().Be("LiveProbe");
        result.SizeMultiplier.Should().BeLessThanOrEqualTo(0.20m);
        result.Reason.Should().Contain("ML drift is holding");
    }

    [Fact]
    public async Task Evaluate_ShouldBlock_WhenHighEdgeTradesStillLoseDespiteOkayOverallBucket()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult());
        await service.RecordPaperState(new[]
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
        result.Reason.Should().MatchRegex("(High-edge outcomes|Exact bucket learning)");
    }

    [Fact]
    public async Task Evaluate_ShouldDemoteToPaper_WhenVenueBoostsKeepUnderperforming()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(symbol: "BTC-USD"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(8m, symbol: "BTC-USD"),
            CreatePaperPosition(7m, symbol: "BTC-USD"),
            CreatePaperPosition(6m, symbol: "BTC-USD"),
            CreatePaperPosition(5m, symbol: "BTC-USD"),
            CreatePaperPosition(4m, symbol: "BTC-USD"),
            CreatePaperPosition(4m, symbol: "BTC-USD"),
            CreatePaperPosition(3m, symbol: "BTC-USD"),
            CreatePaperPosition(2m, symbol: "BTC-USD")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(-4m, slippagePercent: 0.18m, executionLatencyMs: 225, edgeScore: 64m, symbol: "BTC-USD", venueExecutionBias: 1.03m),
            CreateLiveTrade(-3m, slippagePercent: 0.19m, executionLatencyMs: 230, edgeScore: 63m, symbol: "BTC-USD", venueExecutionBias: 1.04m),
            CreateLiveTrade(-2m, slippagePercent: 0.17m, executionLatencyMs: 220, edgeScore: 62m, symbol: "BTC-USD", venueExecutionBias: 1.02m),
            CreateLiveTrade(1m, slippagePercent: 0.18m, executionLatencyMs: 228, edgeScore: 61m, symbol: "BTC-USD", venueExecutionBias: 1.03m)
        };

        var signal = CreateSignal();
        signal.Symbol = "BTC-USD";
        signal.Metadata["Venue"] = "Binance";

        var result = service.Evaluate(signal, CreateDecision(), liveTrades);

        result.Approved.Should().BeFalse();
        result.PromotionStage.Should().Be("Paper");
        result.Reason.Should().MatchRegex("(venue learning blocked promotion|Exact bucket learning|Concordance)");
    }

    [Fact]
    public async Task Evaluate_ShouldForceLiveProbe_WhenTrimmedVenueExecutionStillOutperforms()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(symbol: "EURUSD="));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(18m, symbol: "EURUSD="),
            CreatePaperPosition(16m, symbol: "EURUSD="),
            CreatePaperPosition(15m, symbol: "EURUSD="),
            CreatePaperPosition(14m, symbol: "EURUSD="),
            CreatePaperPosition(13m, symbol: "EURUSD="),
            CreatePaperPosition(12m, symbol: "EURUSD="),
            CreatePaperPosition(11m, symbol: "EURUSD="),
            CreatePaperPosition(10m, symbol: "EURUSD=")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(12m, slippagePercent: 0.01m, executionLatencyMs: 34, edgeScore: 86m, symbol: "EURUSD=", venueExecutionBias: 0.97m),
            CreateLiveTrade(10m, slippagePercent: 0.01m, executionLatencyMs: 32, edgeScore: 84m, symbol: "EURUSD=", venueExecutionBias: 0.96m),
            CreateLiveTrade(9m, slippagePercent: 0.01m, executionLatencyMs: 33, edgeScore: 82m, symbol: "EURUSD=", venueExecutionBias: 0.98m),
            CreateLiveTrade(8m, slippagePercent: 0.01m, executionLatencyMs: 31, edgeScore: 80m, symbol: "EURUSD=", venueExecutionBias: 0.97m)
        };

        var signal = CreateSignal();
        signal.Symbol = "EURUSD=";
        signal.Metadata["Venue"] = "Oanda";

        var result = service.Evaluate(signal, CreateDecision(), liveTrades);

        result.Approved.Should().BeTrue();
        result.PromotionStage.Should().Be("LiveProbe");
        result.SizeMultiplier.Should().BeLessThanOrEqualTo(0.25m);
        result.Reason.Should().Contain("venue learning stays cautious");
    }

    [Fact]
    public async Task Evaluate_ShouldBlock_WhenExactBucketAndVenueAreBothWeak()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordBacktestState(CreateBacktestResult(symbol: "BTC-USD"));
        await service.RecordPaperState(new[]
        {
            CreatePaperPosition(8m, symbol: "BTC-USD"),
            CreatePaperPosition(7m, symbol: "BTC-USD"),
            CreatePaperPosition(6m, symbol: "BTC-USD"),
            CreatePaperPosition(5m, symbol: "BTC-USD"),
            CreatePaperPosition(4m, symbol: "BTC-USD"),
            CreatePaperPosition(4m, symbol: "BTC-USD"),
            CreatePaperPosition(3m, symbol: "BTC-USD"),
            CreatePaperPosition(2m, symbol: "BTC-USD")
        });

        var liveTrades = new[]
        {
            CreateLiveTrade(-12m, slippagePercent: 0.18m, executionLatencyMs: 225, edgeScore: 64m, symbol: "BTC-USD", venueExecutionBias: 1.03m),
            CreateLiveTrade(-10m, slippagePercent: 0.19m, executionLatencyMs: 230, edgeScore: 63m, symbol: "BTC-USD", venueExecutionBias: 1.04m),
            CreateLiveTrade(-8m, slippagePercent: 0.17m, executionLatencyMs: 220, edgeScore: 62m, symbol: "BTC-USD", venueExecutionBias: 1.02m),
            CreateLiveTrade(-6m, slippagePercent: 0.18m, executionLatencyMs: 228, edgeScore: 61m, symbol: "BTC-USD", venueExecutionBias: 1.03m)
        };

        var signal = CreateSignal();
        signal.Symbol = "BTC-USD";
        signal.Metadata["Venue"] = "Binance";

        var result = service.Evaluate(signal, CreateDecision(), liveTrades);

        result.Approved.Should().BeFalse();
        result.PromotionStage.Should().Be("Paper");
        result.Reason.Should().Contain("Concordance blocked promotion");
    }

    [Fact]
    public async Task Evaluate_ShouldBlock_WhenBacktestLadderGateIsMissing()
    {
        var store = new InMemoryStrategyPerformanceStore();
        var service = CreateService(store);
        await service.RecordPaperState(new[]
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
        RegimeTag = "Trending",
        ExitPolicyTag = "TrendRunner"
    };

    private static PaperPosition CreatePaperPosition(decimal pnl, string strategyTag = "TrendConfluence", string regimeTag = "Trending", string exitPolicyTag = "TrendRunner", string symbol = "THYAO") => new()
    {
        Symbol = symbol,
        Side = ThatsMyQuant.Contracts.Enums.TradeSide.Buy,
        EntryPrice = 100m,
        CurrentPrice = 100m,
        Quantity = 1m,
        RealizedPnL = pnl,
        ExitTime = new DateTime(2026, 03, 21, 8, 45, 0, DateTimeKind.Utc),
        StrategyTag = strategyTag,
        RegimeTag = regimeTag,
        ExitPolicyTag = exitPolicyTag
    };

    private static Trade CreateLiveTrade(
        decimal pnl,
        decimal slippagePercent = 0m,
        long executionLatencyMs = 0,
        string strategyTag = "TrendConfluence",
        string regimeTag = "Trending",
        decimal edgeScore = 75m,
        string exitPolicyTag = "TrendRunner",
        decimal finalAllocatorBias = 0m,
        string symbol = "THYAO",
        decimal venueExecutionBias = 0m,
        string modelVersion = "",
        bool featureDriftDetected = false,
        bool featureDriftCritical = false,
        decimal featureDriftMaxZScore = 0m) => new()
    {
        Symbol = symbol,
        Side = TradeSide.Buy,
        EntryPrice = 100m,
        ExitPrice = 100m + pnl,
        Quantity = 1m,
        Status = TradeStatus.Executed,
        StrategyTag = strategyTag,
        RegimeTag = regimeTag,
        ExitPolicyTag = exitPolicyTag,
        EdgeScore = edgeScore,
        EdgeBand = edgeScore >= 85m ? "Full" : edgeScore >= 70m ? "Reduced" : edgeScore >= 55m ? "Probe" : "Reject",
        FinalAllocatorBias = finalAllocatorBias,
        FinalAllocatorScope = finalAllocatorBias > 0m ? "exact symbol-session bucket" : string.Empty,
        VenueExecutionBias = venueExecutionBias,
        VenueExecutionScope = venueExecutionBias > 0m ? "venue execution" : string.Empty,
        MlModelVersion = modelVersion,
        FeatureDriftDetected = featureDriftDetected,
        FeatureDriftCritical = featureDriftCritical,
        FeatureDriftMaxZScore = featureDriftMaxZScore,
        FeatureDriftImpactLabel = featureDriftDetected ? (featureDriftCritical ? "ML drift vetoed setup" : "ML drift trimmed size") : string.Empty,
        EntrySlippagePercent = slippagePercent,
        ExecutionLatencyMs = executionLatencyMs,
        ExitTime = new DateTime(2026, 03, 21, 8, 50, 0, DateTimeKind.Utc)
    };

    private static BacktestResult CreateBacktestResult(
        string symbol = "THYAO",
        string strategyTag = "TrendConfluence",
        string regimeTag = "Trending",
        string exitPolicyTag = "TrendRunner") => new()
    {
        Symbol = symbol,
        TotalTrades = 8,
        WinningTrades = 6,
        LosingTrades = 2,
        TotalPnL = 420m,
        ProfitFactor = 1.45,
        Trades = new List<BacktestTradeResult>
        {
            CreateBacktestTrade(120m, symbol, strategyTag, regimeTag, exitPolicyTag, new DateTime(2026, 03, 21, 8, 0, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(90m, symbol, strategyTag, regimeTag, exitPolicyTag, new DateTime(2026, 03, 21, 8, 5, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(80m, symbol, strategyTag, regimeTag, exitPolicyTag, new DateTime(2026, 03, 21, 8, 10, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(75m, symbol, strategyTag, regimeTag, exitPolicyTag, new DateTime(2026, 03, 21, 8, 15, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(65m, symbol, strategyTag, regimeTag, exitPolicyTag, new DateTime(2026, 03, 21, 8, 20, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(55m, symbol, strategyTag, regimeTag, exitPolicyTag, new DateTime(2026, 03, 21, 8, 25, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(-30m, symbol, strategyTag, regimeTag, exitPolicyTag, new DateTime(2026, 03, 21, 8, 35, 0, DateTimeKind.Utc)),
            CreateBacktestTrade(-35m, symbol, strategyTag, regimeTag, exitPolicyTag, new DateTime(2026, 03, 21, 8, 40, 0, DateTimeKind.Utc))
        }
    };

    private static BacktestTradeResult CreateBacktestTrade(
        decimal pnl,
        string symbol,
        string strategyTag,
        string regimeTag,
        string exitPolicyTag,
        DateTime exitTime) => new()
    {
        EntryTime = exitTime.AddMinutes(-30),
        ExitTime = exitTime,
        Side = ThatsMyQuant.Contracts.Enums.TradeSide.Buy,
        StrategyTag = strategyTag,
        RegimeTag = regimeTag,
        ExitPolicyTag = exitPolicyTag,
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

        public Task<StrategyPerformanceState> LoadAsync() => Task.FromResult(_state);

        public Task SaveAsync(StrategyPerformanceState state) { _state = state; return Task.CompletedTask; }
    }

    private static StrategyPromotionService CreateService(InMemoryStrategyPerformanceStore store)
        => new(store, configuration: BuildPromotionConfig(
            backtestMinTrades: 6,
            backtestMinProfitFactor: 1.15,
            backtestMinWinRate: 0.42,
            paperMinTrades: 8,
            paperMinProfitFactor: 1.20,
            paperMinWinRate: 0.45));

    private static IConfiguration BuildPromotionConfig(
        int backtestMinTrades,
        double backtestMinProfitFactor,
        double backtestMinWinRate,
        int paperMinTrades,
        double paperMinProfitFactor,
        double paperMinWinRate)
    {
        var values = new Dictionary<string, string?>
        {
            ["Promotion:Backtest:MinTrades"] = backtestMinTrades.ToString(),
            ["Promotion:Backtest:MinProfitFactor"] = backtestMinProfitFactor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Promotion:Backtest:MinWinRate"] = backtestMinWinRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Promotion:Paper:MinTrades"] = paperMinTrades.ToString(),
            ["Promotion:Paper:MinProfitFactor"] = paperMinProfitFactor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Promotion:Paper:MinWinRate"] = paperMinWinRate.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
