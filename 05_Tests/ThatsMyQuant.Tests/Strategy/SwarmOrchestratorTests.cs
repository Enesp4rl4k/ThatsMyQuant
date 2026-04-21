using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Tests.TestInfrastructure;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class SwarmOrchestratorTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _modelPath;

    public SwarmOrchestratorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "tmq-swarm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _modelPath = Path.Combine(_tempDirectory, "PredictiveAlphaModel.zip");
        BuildTestModel(_modelPath);
    }

    [Fact]
    public async Task Handle_ShouldRejectSignal_WhenCriticalFeatureDriftIsDetected()
    {
        var driftMonitor = new FeatureDriftMonitor(5, zScoreThreshold: 2.0, minimumSamplesBeforeDrift: 1);
        driftMonitor.SetBaseline(
            new[] { 0d, 0d, 0d, 0d, 0d },
            new[] { 1d, 1d, 1d, 1d, 1d });

        var featureAgent = new FeatureEngineeringAgent();
        featureAgent.CurrentTensor[0] = 5f;
        featureAgent.CurrentTensor[1] = 5f;
        featureAgent.CurrentTensor[2] = 5f;

        var orchestrator = CreateOrchestrator(driftMonitor, featureAgent);
        var request = CreateRequest();
        request.Foundation = new FoundationSnapshot
        {
            BidAskImbalance = 5d,
            TickDirectionRatio = 5d
        };

        var result = await orchestrator.Handle(request, CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectionFlags.Should().HaveFlag(SignalReasonFlags.MachineLearningVeto);
        request.Signal.Metadata["FeatureDriftDetected"].Should().Be("true");
        request.Signal.Metadata["FeatureDriftCritical"].Should().Be("true");
    }

    [Fact]
    public async Task Handle_ShouldReduceSize_WhenFeatureDriftIsElevatedButNotCritical()
    {
        var cleanFeatureAgent = new FeatureEngineeringAgent();
        cleanFeatureAgent.CurrentTensor[0] = 1.5f;
        cleanFeatureAgent.CurrentTensor[1] = 1.2f;
        cleanFeatureAgent.CurrentTensor[2] = 0.9f;
        var cleanMonitor = new FeatureDriftMonitor(5, zScoreThreshold: 3.0, minimumSamplesBeforeDrift: 1);
        cleanMonitor.SetBaseline(
            new[] { 1.5d, 1.2d, 0.9d, 0.6d, 0.3d },
            new[] { 1d, 1d, 1d, 1d, 1d });

        var driftedFeatureAgent = new FeatureEngineeringAgent();
        driftedFeatureAgent.CurrentTensor[0] = 3.2f;
        driftedFeatureAgent.CurrentTensor[1] = 1.2f;
        driftedFeatureAgent.CurrentTensor[2] = 0.9f;
        var driftedMonitor = new FeatureDriftMonitor(5, zScoreThreshold: 3.0, minimumSamplesBeforeDrift: 1);
        driftedMonitor.SetBaseline(
            new[] { 0d, 1.2d, 0.9d, 0.6d, 0.3d },
            new[] { 1d, 1d, 1d, 1d, 1d });

        var cleanOrchestrator = CreateOrchestrator(cleanMonitor, cleanFeatureAgent);
        var driftedOrchestrator = CreateOrchestrator(driftedMonitor, driftedFeatureAgent);

        var cleanRequest = CreateRequest();
        cleanRequest.Foundation = new FoundationSnapshot
        {
            BidAskImbalance = 0.6d,
            TickDirectionRatio = 0.3d
        };

        var cleanResult = await cleanOrchestrator.Handle(cleanRequest, CancellationToken.None);
        var driftedRequest = CreateRequest();
        driftedRequest.Foundation = new FoundationSnapshot
        {
            BidAskImbalance = 0.6d,
            TickDirectionRatio = 0.3d
        };
        var driftedResult = await driftedOrchestrator.Handle(driftedRequest, CancellationToken.None);

        cleanResult.Approved.Should().BeTrue();
        driftedResult.Approved.Should().BeTrue();
        driftedResult.AdjustedKellyFraction.Should().BeLessThan(cleanResult.AdjustedKellyFraction);
        driftedRequest.Signal.Metadata["FeatureDriftDetected"].Should().Be("true");
        driftedRequest.Signal.Metadata["FeatureDriftCritical"].Should().Be("false");
    }

    private SwarmOrchestrator CreateOrchestrator(FeatureDriftMonitor driftMonitor, FeatureEngineeringAgent featureAgent)
    {
        var sentimentService = Substitute.For<ISentimentService>();
        sentimentService.AnalyzeNewsAsync(Arg.Any<string>())
            .Returns(Task.FromResult((0.5d, false, "Supportive backdrop")));

        var sentimentAgent = new SentimentAgent(
            sentimentService,
            new NewsFeedSim(Substitute.For<ILogger<NewsFeedSim>>()),
            Substitute.For<ILogger<SentimentAgent>>());

        var inferenceAgent = new InferenceAgent(driftMonitor, _modelPath);
        var portfolioCache = Substitute.For<IPortfolioStateCache>();
        portfolioCache.GetRecentTrades().Returns(Array.Empty<Trade>());

        var exchangeInfo = Substitute.For<IExchangeInfoProvider>();
        exchangeInfo.GetTickSize(Arg.Any<string>()).Returns(0.01d);
        exchangeInfo.GetLotSize(Arg.Any<string>()).Returns(0.001d);

        var ofiAgent = new OFIExecutionAgent(
            Substitute.For<ILogger<OFIExecutionAgent>>(),
            exchangeInfo);

        var coordinator = Substitute.For<IAgentCoordinator>();
        coordinator.CollectStrategyVotesAsync(
                Arg.Any<Signal>(),
                Arg.Any<PatternSnapshot>(),
                Arg.Any<FoundationSnapshot>(),
                Arg.Any<IReadOnlyList<Candle>>(),
                Arg.Any<MarketRegime>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<StrategyVote>>(Array.Empty<StrategyVote>()));
        coordinator.RunMetaAdaptationAsync(
                Arg.Any<IReadOnlyList<Trade>>(),
                Arg.Any<MarketRegime>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MetaAdaptation.Default));
        coordinator.RunRiskGauntletAsync(
                Arg.Any<Signal>(),
                Arg.Any<TradeDecision>(),
                Arg.Any<IReadOnlyList<Trade>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RiskGuardResult.Pass("Risk.Passed")));

        return new SwarmOrchestrator(
            new RiskAgent(Substitute.For<ILogger<RiskAgent>>()),
            sentimentAgent,
            inferenceAgent,
            featureAgent,
            portfolioCache,
            ofiAgent,
            coordinator,
            Substitute.For<ILogger<SwarmOrchestrator>>());
    }

    private static SwarmNegotiationRequest CreateRequest()
        => new()
        {
            Signal = new Signal
            {
                Symbol = "BTCUSDT",
                Side = TradeSide.Buy,
                Price = 100m,
                Volume24h = 5_000_000m
            },
            InitialDecision = new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 80,
                TakeProfit = 110m,
                PositionSizePercent = 0.03m
            },
            Risk = RiskParameters.Default,
            Candles = CreateCandles()
        };

    private static List<Candle> CreateCandles()
        => Enumerable.Range(0, 60)
            .Select(i => new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-60 + i),
                Open = 100m + (i * 0.1m),
                High = 101m + (i * 0.1m),
                Low = 99m + (i * 0.1m),
                Close = 100.5m + (i * 0.1m),
                Volume = 1_000_000
            })
            .ToList();

    private static void BuildTestModel(string modelPath)
    {
        TestModelCache.EnsureModel(modelPath, "swarm-v1", cachePath =>
        {
            var trainer = new AlphaModelTrainer(Substitute.For<ILogger<AlphaModelTrainer>>());
            var data = Enumerable.Range(0, 160)
                .Select(i =>
                {
                    var positive = i % 2 == 0;
                    var baseValue = positive ? 1.5f : -1.5f;
                    return new CandleMlData
                    {
                        NextClosePrice = positive ? 1f : -1f,
                        Features =
                        [
                            baseValue,
                            baseValue * 0.8f,
                            baseValue * 0.6f,
                            baseValue * 0.4f,
                            baseValue * 0.2f
                        ]
                    };
                })
                .ToList();

            trainer.TrainAndSaveModel(data, cachePath);
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
