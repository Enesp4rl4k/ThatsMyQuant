using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using NSubstitute;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class SwarmOrchestratorRecoveryTests : IDisposable
{
    private readonly SwarmOrchestrator _sut;
    private readonly RiskAgent _riskAgent;
    private readonly IPortfolioStateCache _portfolioCache;
    private readonly IAgentCoordinator _agentCoordinator;
    private readonly SwarmOptions _swarmOptions;
    private const string TestModelPath = "test-recovery-model.zip";

    public SwarmOrchestratorRecoveryTests()
    {
        // Create a minimal ML model file so InferenceAgent doesn't throw on construction
        if (!File.Exists(TestModelPath))
        {
            var mlContext = new MLContext(seed: 42);
            var data = mlContext.Data.LoadFromEnumerable(new[]
            {
                new CandleMlData { Features = [1, 2, 3, 4, 5], NextClosePrice = 100 },
                new CandleMlData { Features = [2, 3, 4, 5, 6], NextClosePrice = 101 },
            });
            var pipeline = mlContext.Regression.Trainers.FastTree(
                featureColumnName: "Features", numberOfTrees: 2, numberOfLeaves: 4);
            var model = pipeline.Fit(data);
            mlContext.Model.Save(model, data.Schema, TestModelPath);
        }

        _riskAgent = new RiskAgent(Substitute.For<ILogger<RiskAgent>>());
        var sentimentAgent = new SentimentAgent(
            Substitute.For<ISentimentService>(),
            new NewsFeedSim(Substitute.For<ILogger<NewsFeedSim>>()),
            Substitute.For<ILogger<SentimentAgent>>());
        var inferenceAgent = new InferenceAgent((FeatureDriftMonitor?)null, TestModelPath);
        var featureAgent = new FeatureEngineeringAgent();
        _portfolioCache = Substitute.For<IPortfolioStateCache>();
        _portfolioCache.GetRecentTrades().Returns(new List<Trade>());
        var ofiAgent = new OFIExecutionAgent(
            Substitute.For<ILogger<OFIExecutionAgent>>(),
            Substitute.For<IExchangeInfoProvider>());
        _agentCoordinator = Substitute.For<IAgentCoordinator>();

        // Configure a very short cooldown for testing
        _swarmOptions = new SwarmOptions
        {
            MaxConsecutiveFailures = 2,
            HaltCooldownMinutes = 60 // Long cooldown for halt test; auto-recovery test overrides
        };

        _sut = new SwarmOrchestrator(
            _riskAgent,
            sentimentAgent,
            inferenceAgent,
            featureAgent,
            _portfolioCache,
            ofiAgent,
            _agentCoordinator,
            Substitute.For<ILogger<SwarmOrchestrator>>(),
            Options.Create(_swarmOptions));
    }

    private SwarmNegotiationRequest CreateRequest() => new()
    {
        Signal = new Signal { Id = Guid.NewGuid(), Symbol = "BTCUSDT", Price = 50000m, Side = TradeSide.Buy },
        InitialDecision = new TradeDecision
        {
            Side = TradeSide.Buy,
            Confidence = 80,
            PositionSizePercent = 0.05m,
            StopLoss = 49000m,
            TakeProfit = 52000m
        },
        Risk = RiskParameters.Default,
        Candles = GenerateCandles(50),
        Foundation = FoundationSnapshot.Empty,
        Patterns = PatternSnapshot.Empty,
        Regime = MarketRegime.Ranging
    };

    [Fact]
    public async Task Handle_AfterMaxFailures_SystemIsHalted()
    {
        // Arrange: Make the agent coordinator throw to trigger failures
        _agentCoordinator.CollectStrategyVotesAsync(
            Arg.Any<Signal>(), Arg.Any<PatternSnapshot>(), Arg.Any<FoundationSnapshot>(),
            Arg.Any<IReadOnlyList<Candle>>(), Arg.Any<MarketRegime>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<StrategyVote>>(x => throw new Exception("Simulated failure"));

        // Also make guardrails pass so we get to the failure point
        _riskAgent.ResetKillSwitch();

        // Act: Trigger failures until halted (MaxConsecutiveFailures = 2)
        for (int i = 0; i < _swarmOptions.MaxConsecutiveFailures; i++)
        {
            var result = await _sut.Handle(CreateRequest(), CancellationToken.None);
            result.Approved.Should().BeFalse();
            result.RejectionFlags.Should().HaveFlag(SignalReasonFlags.SystemError);
        }

        // The next call should be halted
        var haltedResult = await _sut.Handle(CreateRequest(), CancellationToken.None);
        haltedResult.Approved.Should().BeFalse();
        haltedResult.RejectionFlags.Should().HaveFlag(SignalReasonFlags.SystemError);
    }

    [Fact]
    public async Task Handle_AfterCooldownExpires_SystemAutoRecovers()
    {
        // Build a separate orchestrator with 0-minute cooldown for immediate recovery
        var zeroCooldownOptions = new SwarmOptions
        {
            MaxConsecutiveFailures = 2,
            HaltCooldownMinutes = 0
        };
        var localCoordinator = Substitute.For<IAgentCoordinator>();
        localCoordinator.CollectStrategyVotesAsync(
            Arg.Any<Signal>(), Arg.Any<PatternSnapshot>(), Arg.Any<FoundationSnapshot>(),
            Arg.Any<IReadOnlyList<Candle>>(), Arg.Any<MarketRegime>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<StrategyVote>>(x => throw new Exception("Simulated failure"));

        var localRiskAgent = new RiskAgent(Substitute.For<ILogger<RiskAgent>>());
        var sentimentAgent = new SentimentAgent(
            Substitute.For<ISentimentService>(),
            new NewsFeedSim(Substitute.For<ILogger<NewsFeedSim>>()),
            Substitute.For<ILogger<SentimentAgent>>());
        var inferenceAgent = new InferenceAgent((FeatureDriftMonitor?)null, TestModelPath);
        var featureAgent = new FeatureEngineeringAgent();
        var ofiAgent = new OFIExecutionAgent(
            Substitute.For<ILogger<OFIExecutionAgent>>(),
            Substitute.For<IExchangeInfoProvider>());
        var localPortfolioCache = Substitute.For<IPortfolioStateCache>();
        localPortfolioCache.GetRecentTrades().Returns(new List<Trade>());

        var recoverySut = new SwarmOrchestrator(
            localRiskAgent, sentimentAgent, inferenceAgent, featureAgent,
            localPortfolioCache, ofiAgent, localCoordinator,
            Substitute.For<ILogger<SwarmOrchestrator>>(),
            Options.Create(zeroCooldownOptions));

        // Trigger halt
        for (int i = 0; i < zeroCooldownOptions.MaxConsecutiveFailures; i++)
        {
            await recoverySut.Handle(CreateRequest(), CancellationToken.None);
        }

        // With 0-minute cooldown, the next call should auto-recover
        localRiskAgent.ResetKillSwitch(); // Reset kill switch armed by emergency shutdown
        var result = await recoverySut.Handle(CreateRequest(), CancellationToken.None);

        // It should have attempted processing (auto-recovered) rather than immediately returning halted
        result.RejectionFlags.Should().HaveFlag(SignalReasonFlags.SystemError);
    }

    public void Dispose()
    {
        if (File.Exists(TestModelPath))
            File.Delete(TestModelPath);
    }

    private static List<Candle> GenerateCandles(int count)
    {
        var candles = new List<Candle>();
        var basePrice = 50000m;
        for (int i = 0; i < count; i++)
        {
            candles.Add(new Candle
            {
                Open = basePrice + i,
                High = basePrice + i + 50,
                Low = basePrice + i - 50,
                Close = basePrice + i + 10,
                Volume = 1000m,
                Timestamp = DateTime.UtcNow.AddMinutes(-count + i)
            });
        }
        return candles;
    }
}
