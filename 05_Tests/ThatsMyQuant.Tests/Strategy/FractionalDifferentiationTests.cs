using System;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class FractionalDifferentiationTests
{
    [Fact]
    public void ComputeWeights_DEqualsZero_ReturnsIdentityFilter()
    {
        // d = 0 means "no differencing" → only ω_0 = 1 (recurrence yields zeros after).
        var weights = FractionalDifferentiation.ComputeWeights(d: 0.0);
        weights.Length.Should().Be(1);
        weights[0].Should().Be(1.0);
    }

    [Fact]
    public void ComputeWeights_DEqualsOne_ApproximatesFirstDifference()
    {
        // d = 1 → ω_0 = 1, ω_1 = -1, all subsequent are zero.
        // Recurrence: ω_1 = -1 * (1 - 0) / 1 = -1. ω_2 = -(-1) * (1 - 1) / 2 = 0. Truncates.
        var weights = FractionalDifferentiation.ComputeWeights(d: 1.0);
        weights.Length.Should().BeGreaterOrEqualTo(2);
        weights[0].Should().BeApproximately(1.0, 1e-12);
        weights[1].Should().BeApproximately(-1.0, 1e-12);
    }

    [Fact]
    public void ComputeWeights_DInBetween_AllMagnitudesShrink()
    {
        // For d ∈ (0,1), |ω_k| should decrease (eventually) — the recurrence ratio
        // |(d - k + 1) / k| → (k - d - 1) / k → 1 from below, so weights shrink.
        var weights = FractionalDifferentiation.ComputeWeights(d: 0.5);
        weights.Length.Should().BeGreaterThan(2);
        // Tail magnitudes should be smaller than head magnitudes for moderate k.
        Math.Abs(weights[^1]).Should().BeLessThan(Math.Abs(weights[1]));
    }

    [Fact]
    public void ComputeWeights_DOutOfRange_Throws()
    {
        Action below = () => FractionalDifferentiation.ComputeWeights(d: -0.1);
        below.Should().Throw<ArgumentOutOfRangeException>();
        Action above = () => FractionalDifferentiation.ComputeWeights(d: 1.5);
        above.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Difference_DEqualsOne_MatchesFirstDifference()
    {
        // Should equal x_t − x_{t-1} (well-known integer case).
        var series = new double[] { 100.0, 101.5, 99.8, 102.3, 105.0 };
        var differenced = FractionalDifferentiation.Difference(series, d: 1.0);

        // First entry NaN (insufficient lookback for the 2-tap filter).
        double.IsNaN(differenced[0]).Should().BeTrue();
        differenced[1].Should().BeApproximately(1.5, 1e-12);
        differenced[2].Should().BeApproximately(-1.7, 1e-12);
        differenced[3].Should().BeApproximately(2.5, 1e-12);
        differenced[4].Should().BeApproximately(2.7, 1e-12);
    }

    [Fact]
    public void Difference_DEqualsZero_PreservesSeries()
    {
        // d = 0 → only ω_0 = 1 → output = input.
        var series = new double[] { 10.0, 20.0, 30.0, 40.0 };
        var output = FractionalDifferentiation.Difference(series, d: 0.0);
        for (int i = 0; i < series.Length; i++)
        {
            output[i].Should().BeApproximately(series[i], 1e-12);
        }
    }

    [Fact]
    public void Difference_PreservesCorrelationWithLevelComparedToFirstDifference()
    {
        // Lopez de Prado's central claim: on a non-stationary series (random walk),
        // fractional differencing preserves the correlation between the differenced
        // output and the original level much better than first-differencing does.
        // First-diff destroys memory (corr near 0); fractional diff with moderate d
        // retains substantial correlation with the original level.
        var rng = new Random(123);
        int n = 800;
        var series = new double[n];
        series[0] = 100.0;
        for (int i = 1; i < n; i++)
        {
            series[i] = series[i - 1] + (rng.NextDouble() - 0.5) * 1.0;
        }

        // Use a relaxed tolerance for d = 0.4 — the weight tail decays like k^{-1.4}
        // and the default 1e-5 tolerance produces a window longer than the series.
        var fullDiff = FractionalDifferentiation.Difference(series, d: 1.0);
        var fracDiff = FractionalDifferentiation.Difference(series, d: 0.4, tolerance: 1e-3);

        double corrFullVsLevel = CorrelationIgnoringNan(fullDiff, series);
        double corrFracVsLevel = CorrelationIgnoringNan(fracDiff, series);

        // Fractional diff should retain |corr| > 0.5 with the level; first diff is near 0.
        Math.Abs(corrFracVsLevel).Should().BeGreaterThan(Math.Abs(corrFullVsLevel) + 0.3,
            "fractional differencing preserves memory while still reducing non-stationarity");
    }

    [Fact]
    public void Difference_OutputBeforeWindow_IsNaN()
    {
        var weights = FractionalDifferentiation.ComputeWeights(d: 0.5);
        int k = weights.Length;
        var series = new double[k * 2];
        for (int i = 0; i < series.Length; i++) series[i] = i + 1.0;

        var output = FractionalDifferentiation.Difference(series, d: 0.5);
        for (int i = 0; i < k - 1; i++)
        {
            double.IsNaN(output[i]).Should().BeTrue($"index {i} should be NaN (before window full)");
        }
        double.IsNaN(output[k - 1]).Should().BeFalse();
    }

    [Fact]
    public void FindMinimumOrder_RandomWalk_RequiresLargeD()
    {
        // Pure random walk needs heavy differencing (d near 1) for stationarity.
        var rng = new Random(7);
        int n = 600;
        var series = new double[n];
        for (int i = 1; i < n; i++)
        {
            series[i] = series[i - 1] + (rng.NextDouble() - 0.5) * 0.02;
        }
        double minD = FractionalDifferentiation.FindMinimumOrder(series, varianceRatioCeiling: 2.0);
        minD.Should().BeGreaterThan(0.3, "random walk needs substantial differencing");
    }

    [Fact]
    public void FindMinimumOrder_StationarySeries_AcceptsLowD()
    {
        // White noise is already stationary → tiny d should pass.
        var rng = new Random(11);
        int n = 600;
        var series = new double[n];
        for (int i = 0; i < n; i++) series[i] = (rng.NextDouble() - 0.5) * 0.02;

        double minD = FractionalDifferentiation.FindMinimumOrder(series, varianceRatioCeiling: 2.0);
        minD.Should().BeLessThan(0.4, "stationary input does not need much differencing");
    }

    [Fact]
    public void ApplyFixedWindow_NullSeries_Throws()
    {
        var weights = FractionalDifferentiation.ComputeWeights(0.5);
        Action act = () => FractionalDifferentiation.ApplyFixedWindow(null!, weights);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ApplyFixedWindow_EmptyWeights_Throws()
    {
        var series = new double[] { 1, 2, 3 };
        Action act = () => FractionalDifferentiation.ApplyFixedWindow(series, Array.Empty<double>());
        act.Should().Throw<ArgumentException>();
    }

    private static double CorrelationIgnoringNan(double[] a, double[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double sumA = 0, sumB = 0;
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(a[i])) continue;
            sumA += a[i]; sumB += b[i]; count++;
        }
        if (count < 2) return 0;
        double meanA = sumA / count;
        double meanB = sumB / count;

        double cov = 0, varA = 0, varB = 0;
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(a[i])) continue;
            double da = a[i] - meanA;
            double db = b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }
        double denom = Math.Sqrt(varA * varB);
        return denom > 0 ? cov / denom : 0;
    }

    private static double LagOneAutocorrelation(double[] series)
    {
        double sum = 0;
        int count = 0;
        for (int i = 0; i < series.Length; i++)
        {
            if (double.IsNaN(series[i])) continue;
            sum += series[i]; count++;
        }
        if (count < 2) return 0.0;
        double mean = sum / count;

        double num = 0, den = 0;
        for (int i = 0; i < series.Length; i++)
        {
            if (double.IsNaN(series[i])) continue;
            double dev = series[i] - mean;
            den += dev * dev;
            if (i > 0 && !double.IsNaN(series[i - 1]))
            {
                num += dev * (series[i - 1] - mean);
            }
        }
        return den > 0 ? num / den : 0.0;
    }
}
