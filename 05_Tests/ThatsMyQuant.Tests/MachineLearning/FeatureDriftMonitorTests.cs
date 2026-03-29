using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.MachineLearning;
using Xunit;

namespace ThatsMyQuant.Tests.MachineLearning;

public class FeatureDriftMonitorTests
{
    [Fact]
    public void SetBaseline_ShouldExposeConfiguredBaselineInStats()
    {
        var monitor = new FeatureDriftMonitor(5, minimumSamplesBeforeDrift: 1);

        monitor.SetBaseline(
            new[] { 0.1, 0.2, 0.3, 0.4, 0.5 },
            new[] { 1.0, 1.1, 1.2, 1.3, 1.4 });

        var stats = monitor.GetStats(3);

        stats.BaselineConfigured.Should().BeTrue();
        stats.BaselineMean.Should().Be(0.4);
        stats.BaselineStdDev.Should().Be(1.3);
    }

    [Fact]
    public void UpdateAll_ShouldFlagCriticalDrift_WhenBaselineDeviationIsExtreme()
    {
        var logger = Substitute.For<ILogger<FeatureDriftMonitor>>();
        var monitor = new FeatureDriftMonitor(5, zScoreThreshold: 2.0, minimumSamplesBeforeDrift: 1, logger: logger);
        monitor.SetBaseline(
            new[] { 0.0, 0.0, 0.0, 0.0, 0.0 },
            new[] { 1.0, 1.0, 1.0, 1.0, 1.0 });

        var report = monitor.UpdateAll(new float[] { 5f, 5f, 0.1f, 0.1f, 0.1f });

        report.DriftedCount.Should().BeGreaterThan(0);
        report.IsCritical.Should().BeTrue();
        report.MaxZScore.Should().BeGreaterThan(3.0);
    }

    [Fact]
    public void Reset_ShouldClearDriftFlagAndLastZScore()
    {
        var monitor = new FeatureDriftMonitor(5, zScoreThreshold: 2.0, minimumSamplesBeforeDrift: 1);
        monitor.SetBaseline(
            new[] { 0.0, 0.0, 0.0, 0.0, 0.0 },
            new[] { 1.0, 1.0, 1.0, 1.0, 1.0 });

        monitor.Update(0, 5.0);
        monitor.Reset(0);

        var stats = monitor.GetStats(0);
        stats.DriftDetected.Should().BeFalse();
        stats.LastZScore.Should().Be(0);
    }
}
