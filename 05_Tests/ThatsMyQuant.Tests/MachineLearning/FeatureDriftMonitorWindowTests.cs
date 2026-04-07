using FluentAssertions;
using ThatsMyQuant.Application.MachineLearning;
using Xunit;

namespace ThatsMyQuant.Tests.MachineLearning;

public class FeatureDriftMonitorWindowTests
{
    [Fact]
    public void Update_BeyondWindowSize_ShouldDecaySampleCount()
    {
        // Arrange: Small window for test
        var sut = new FeatureDriftMonitor(featureCount: 2, maxWindowSize: 100);

        // Act: Feed 200 samples (2x the window)
        for (int i = 0; i < 200; i++)
        {
            sut.Update(0, 1.0 + i * 0.001);
        }

        // Assert: Sample count should be capped at window size
        var stats = sut.GetStats(0);
        stats.SampleCount.Should().BeLessThanOrEqualTo(100,
            "sample count should be capped at maxWindowSize after decay");
    }

    [Fact]
    public void Update_WithinWindowSize_ShouldNotDecay()
    {
        var sut = new FeatureDriftMonitor(featureCount: 1, maxWindowSize: 1000);

        for (int i = 0; i < 500; i++)
        {
            sut.Update(0, 1.0);
        }

        var stats = sut.GetStats(0);
        stats.SampleCount.Should().Be(500, "no decay should occur within window bounds");
    }

    [Fact]
    public void UpdateAll_ShouldReturnDriftReport()
    {
        var sut = new FeatureDriftMonitor(featureCount: 3, maxWindowSize: 100);

        // Set a baseline to enable z-score drift detection
        sut.SetBaseline(
            new double[] { 0.0, 0.0, 0.0 },
            new double[] { 1.0, 1.0, 1.0 });

        // Feed enough stable samples
        for (int i = 0; i < 30; i++)
        {
            sut.UpdateAll(new float[] { 0.1f, 0.1f, 0.1f });
        }

        // Now feed an extreme outlier
        var report = sut.UpdateAll(new float[] { 0.1f, 100.0f, 0.1f });

        report.TotalFeatures.Should().Be(3);
        report.DriftedCount.Should().BeGreaterThan(0, "extreme value should trigger drift");
        report.MaxZScore.Should().BeGreaterThan(3.0, "z-score should exceed threshold for outlier");
    }

    [Fact]
    public void Reset_ShouldClearDriftState()
    {
        var sut = new FeatureDriftMonitor(featureCount: 2, maxWindowSize: 100);

        sut.SetBaseline(new double[] { 0.0, 0.0 }, new double[] { 1.0, 1.0 });

        // Trigger drift
        for (int i = 0; i < 30; i++) sut.Update(0, 0.1);
        sut.Update(0, 100.0); // Extreme outlier

        var statsBefore = sut.GetStats(0);
        statsBefore.DriftDetected.Should().BeTrue();

        // Reset
        sut.Reset(0);

        var statsAfter = sut.GetStats(0);
        statsAfter.DriftDetected.Should().BeFalse("drift state should be cleared after reset");
        statsAfter.LastZScore.Should().Be(0.0);
    }

    [Fact]
    public void Update_NaN_ShouldBeIgnored()
    {
        var sut = new FeatureDriftMonitor(featureCount: 1, maxWindowSize: 100);

        sut.Update(0, 1.0);
        sut.Update(0, double.NaN);
        sut.Update(0, double.PositiveInfinity);

        var stats = sut.GetStats(0);
        stats.SampleCount.Should().Be(1, "NaN and Infinity should be ignored");
    }

    [Fact]
    public void Update_InvalidFeatureIndex_ShouldReturnFalse()
    {
        var sut = new FeatureDriftMonitor(featureCount: 3, maxWindowSize: 100);

        sut.Update(-1, 1.0).Should().BeFalse();
        sut.Update(3, 1.0).Should().BeFalse();
        sut.Update(99, 1.0).Should().BeFalse();
    }
}
