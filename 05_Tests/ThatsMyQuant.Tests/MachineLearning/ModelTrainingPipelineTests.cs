using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.MachineLearning;

public class ModelTrainingPipelineTests : IDisposable
{
    private readonly string _artifactDirectory;

    public ModelTrainingPipelineTests()
    {
        _artifactDirectory = Path.Combine(Path.GetTempPath(), "tmq-ml-pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_artifactDirectory);
    }

    [Fact]
    public void GetDatasetReport_ShouldReflectBalancedTrainableDataset()
    {
        var (pipeline, featureAgent, _) = CreatePipeline();
        SeedSeparableDataset(pipeline, featureAgent, 60);

        var report = pipeline.GetDatasetReport();

        report.TotalSamples.Should().Be(60);
        report.PositiveSamples.Should().Be(30);
        report.NegativeSamples.Should().Be(30);
        report.IsTrainable.Should().BeTrue();
        report.UniqueSymbols.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Train_ShouldPersistArtifact_AndAllowPredictionAfterReload()
    {
        var (pipeline, featureAgent, _) = CreatePipeline();
        SeedSeparableDataset(pipeline, featureAgent, 80);

        var result = pipeline.Train();

        result.Success.Should().BeTrue();
        result.TrainSamples.Should().BeGreaterThan(0);
        result.TestSamples.Should().BeGreaterThan(0);
        result.ArtifactPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(result.ArtifactPath).Should().BeTrue();

        var livePrediction = pipeline.TryPredictProbability(
            new float[] { 2.2f, 1.7f, 1.4f, 0.8f, 1.1f },
            out var liveProbability,
            out var livePredictedPositive);

        livePrediction.Should().BeTrue();
        livePredictedPositive.Should().BeTrue();
        liveProbability.Should().BeGreaterThan(0.5);

        var (reloadedPipeline, _, _) = CreatePipeline();
        reloadedPipeline.TryLoadPersistedModel().Should().BeTrue();

        var reloadPrediction = reloadedPipeline.TryPredictProbability(
            new float[] { -2.1f, -1.5f, -1.2f, -0.8f, -1.1f },
            out var reloadProbability,
            out var reloadPredictedPositive);

        reloadPrediction.Should().BeTrue();
        reloadPredictedPositive.Should().BeFalse();
        reloadProbability.Should().BeLessThan(0.5);
    }

    [Fact]
    public void TryPredictProbability_ShouldReturnFalse_WhenCriticalBaselineDriftIsDetected()
    {
        var (pipeline, featureAgent, driftMonitor) = CreatePipeline();
        SeedSeparableDataset(pipeline, featureAgent, 80);

        pipeline.Train().Success.Should().BeTrue();
        driftMonitor.HasBaseline.Should().BeTrue();

        var predicted = pipeline.TryPredictProbability(
            new float[] { 25f, 25f, 25f, 25f, 25f },
            out _,
            out _);

        predicted.Should().BeFalse();
    }

    private (ModelTrainingPipeline Pipeline, FeatureEngineeringAgent FeatureAgent, FeatureDriftMonitor DriftMonitor) CreatePipeline()
    {
        var logger = Substitute.For<ILogger<ModelTrainingPipeline>>();
        var featureAgent = new FeatureEngineeringAgent();
        var driftMonitor = new FeatureDriftMonitor(5, minimumSamplesBeforeDrift: 1);
        return (new ModelTrainingPipeline(logger, featureAgent, driftMonitor, _artifactDirectory), featureAgent, driftMonitor);
    }

    private static void SeedSeparableDataset(
        ModelTrainingPipeline pipeline,
        FeatureEngineeringAgent featureAgent,
        int sampleCount)
    {
        for (var i = 0; i < sampleCount; i++)
        {
            var isPositive = i % 2 == 0;
            featureAgent.CurrentTensor[0] = isPositive ? 2.0f : -2.0f;
            featureAgent.CurrentTensor[1] = isPositive ? 1.5f : -1.5f;
            featureAgent.CurrentTensor[2] = isPositive ? 1.2f : -1.2f;
            featureAgent.CurrentTensor[3] = isPositive ? 0.9f : -0.9f;
            featureAgent.CurrentTensor[4] = isPositive ? 1.0f : -1.0f;

            pipeline.RecordSample(
                new Trade { Symbol = isPositive ? "BTCUSDT" : "ETHUSDT" },
                isPositive ? 10m : -10m);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_artifactDirectory))
        {
            Directory.Delete(_artifactDirectory, recursive: true);
        }
    }
}
