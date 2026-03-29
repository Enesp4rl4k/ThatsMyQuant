using System;
using System.Collections.Generic;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class EngleGrangerDetectorTests
{
    [Fact]
    public void TestCointegration_InsufficientData_ReturnsFalse()
    {
        var seriesX = new decimal[] { 1m, 2m, 3m };
        var seriesY = new decimal[] { 2m, 4m, 6m };

        var result = EngleGrangerDetector.TestCointegration(seriesY, seriesX);

        result.IsCointegrated.Should().BeFalse();
    }

    [Fact]
    public void TestCointegration_PerfectlyCointegrated_ReturnsTrue()
    {
        // Generate a random walk X
        var rng = new Random(42);
        var seriesX = new decimal[100];
        var seriesY = new decimal[100];

        seriesX[0] = 100m;
        
        // Cointegration parameters
        decimal alpha = 5m;
        decimal beta = 2m; // Y = 5 + 2X + stationary noise
        
        for (int i = 1; i < 100; i++)
        {
            seriesX[i] = seriesX[i - 1] + (decimal)(rng.NextDouble() - 0.5) * 2; // Random walk X
        }

        // Generate Y with stationary mean-reverting noise
        decimal noise = 0;
        for (int i = 0; i < 100; i++)
        {
            // O-U process for noise (mean reverting to 0)
            noise = noise * 0.5m + (decimal)(rng.NextDouble() - 0.5); 
            seriesY[i] = alpha + beta * seriesX[i] + noise;
        }

        var result = EngleGrangerDetector.TestCointegration(seriesY, seriesX);

        result.IsCointegrated.Should().BeTrue();
        result.HedgeRatio.Should().BeApproximately(2m, 0.1m); // Recovered Beta
        result.SpreadAverage.Should().BeApproximately(0m, 0.1m);
        result.AdfTestStatistic.Should().BeLessThan(-0.05m); // Gamma should be highly negative, showing mean reversion
    }

    [Fact]
    public void TestCointegration_TwoIndependentRandomWalks_ReturnsFalse()
    {
        var rng = new Random(1337); // Different seed
        var seriesX = new decimal[1000]; // 1000 samples to prevent small-sample spurious over-fitting
        var seriesY = new decimal[1000];

        seriesX[0] = 100m;
        seriesY[0] = 50m;
        
        for (int i = 1; i < 1000; i++)
        {
            seriesX[i] = seriesX[i - 1] + (decimal)(rng.NextDouble() - 0.5);
            seriesY[i] = seriesY[i - 1] + (decimal)(rng.NextDouble() - 0.5); // Completely independent
        }

        var result = EngleGrangerDetector.TestCointegration(seriesY, seriesX);

        result.IsCointegrated.Should().BeFalse();
        // The ADF statistic (gamma) for a thick independent random walk should definitely fail the strict -0.25 mean reversion check
        result.AdfTestStatistic.Should().BeGreaterThanOrEqualTo(-0.25m); 
    }
}
