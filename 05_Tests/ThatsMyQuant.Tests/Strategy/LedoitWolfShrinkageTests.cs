using System;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class LedoitWolfShrinkageTests
{
    [Fact]
    public void Estimate_NullInput_Throws()
    {
        Action act = () => LedoitWolfShrinkage.Estimate((double[,])null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Estimate_TooFewObservations_Throws()
    {
        var returns = new double[1, 3];
        Action act = () => LedoitWolfShrinkage.Estimate(returns);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Estimate_DeltaInUnitInterval()
    {
        var returns = GenerateRandomReturns(seed: 7, t: 120, n: 8, vol: 0.02);
        var result = LedoitWolfShrinkage.Estimate(returns);

        result.Delta.Should().BeGreaterThanOrEqualTo(0.0);
        result.Delta.Should().BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public void Estimate_ResultIsSymmetric()
    {
        var returns = GenerateRandomReturns(seed: 13, t: 80, n: 5, vol: 0.015);
        var result = LedoitWolfShrinkage.Estimate(returns);
        int n = result.Covariance.GetLength(0);

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                result.Covariance[i, j].Should().BeApproximately(result.Covariance[j, i], 1e-12);
            }
        }
    }

    [Fact]
    public void Estimate_DiagonalPreserved_TargetMatchesSampleVariance()
    {
        // The constant-correlation target preserves diagonal of S, so F[i,i] == S[i,i].
        var returns = GenerateRandomReturns(seed: 21, t: 60, n: 4, vol: 0.025);
        var result = LedoitWolfShrinkage.Estimate(returns);
        int n = result.Covariance.GetLength(0);

        for (int i = 0; i < n; i++)
        {
            result.Target[i, i].Should().BeApproximately(result.SampleCovariance[i, i], 1e-12);
        }
    }

    [Fact]
    public void Estimate_PerfectIidGaussian_ProducesPositiveDelta()
    {
        // True correlation matrix is identity. Sample correlations will be small but
        // noisy; shrinkage toward the constant-correlation target should pull them
        // toward zero (target r̄ ≈ 0 in expectation), reducing estimation error.
        var returns = GenerateRandomReturns(seed: 33, t: 50, n: 10, vol: 0.02);
        var result = LedoitWolfShrinkage.Estimate(returns);

        result.Delta.Should().BeGreaterThan(0.0,
            "noisy sample covariance with N close to T benefits from shrinkage");
    }

    [Fact]
    public void Estimate_ImprovesConditionNumber_InHighDimSmallSampleRegime()
    {
        // T just barely > N: sample covariance is severely ill-conditioned. Shrinkage
        // toward constant-correlation target should produce a numerically saner matrix.
        var returns = GenerateRandomReturns(seed: 41, t: 30, n: 20, vol: 0.018);
        var result = LedoitWolfShrinkage.Estimate(returns);

        var sampleCondition = LedoitWolfShrinkage.ConditionNumber(result.SampleCovariance);
        var shrunkCondition = LedoitWolfShrinkage.ConditionNumber(result.Covariance);

        shrunkCondition.Should().BeLessThan(sampleCondition,
            "shrinkage must reduce condition number in the small-sample regime");
    }

    [Fact]
    public void Estimate_HighlyCorrelatedAssets_TargetCorrelationIsHigh()
    {
        // Two perfectly correlated assets should produce r̄ near +1.
        var rng = new Random(57);
        int t = 200;
        var returns = new double[t, 3];
        for (int i = 0; i < t; i++)
        {
            double common = NextGaussian(rng) * 0.02;
            double idiosyncratic = NextGaussian(rng) * 0.001;
            returns[i, 0] = common;
            returns[i, 1] = common + idiosyncratic;
            returns[i, 2] = common - idiosyncratic;
        }

        var result = LedoitWolfShrinkage.Estimate(returns);
        result.AverageCorrelation.Should().BeGreaterThan(0.9,
            "near-identical return series have correlation ≈ 1");
    }

    [Fact]
    public void Estimate_JaggedAndRectangularProduceSameResult()
    {
        var rect = GenerateRandomReturns(seed: 67, t: 40, n: 4, vol: 0.02);
        int t = rect.GetLength(0);
        int n = rect.GetLength(1);
        var jagged = new double[t][];
        for (int i = 0; i < t; i++)
        {
            jagged[i] = new double[n];
            for (int j = 0; j < n; j++) jagged[i][j] = rect[i, j];
        }

        var rectResult = LedoitWolfShrinkage.Estimate(rect);
        var jaggedResult = LedoitWolfShrinkage.Estimate(jagged);

        rectResult.Delta.Should().BeApproximately(jaggedResult.Delta, 1e-12);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                rectResult.Covariance[i, j].Should().BeApproximately(jaggedResult.Covariance[i, j], 1e-12);
            }
        }
    }

    [Fact]
    public void Estimate_Convex_ShrunkBetweenSampleAndTarget()
    {
        // For each (i,j), Σ̂[i,j] = δ·F[i,j] + (1-δ)·S[i,j] should sit between F and S.
        var returns = GenerateRandomReturns(seed: 79, t: 100, n: 6, vol: 0.02);
        var result = LedoitWolfShrinkage.Estimate(returns);
        int n = result.Covariance.GetLength(0);
        double delta = result.Delta;

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double expected = delta * result.Target[i, j] + (1 - delta) * result.SampleCovariance[i, j];
                result.Covariance[i, j].Should().BeApproximately(expected, 1e-10);
            }
        }
    }

    [Fact]
    public void Estimate_DegenerateZeroVariance_DoesNotThrow()
    {
        // One asset has zero variance (all returns identical). Estimator should
        // gracefully degrade — no division by zero, no NaN propagation.
        int t = 30;
        int n = 3;
        var returns = new double[t, n];
        var rng = new Random(91);
        for (int i = 0; i < t; i++)
        {
            returns[i, 0] = 0.01; // constant
            returns[i, 1] = NextGaussian(rng) * 0.02;
            returns[i, 2] = NextGaussian(rng) * 0.015;
        }

        Action act = () => LedoitWolfShrinkage.Estimate(returns);
        act.Should().NotThrow();

        var result = LedoitWolfShrinkage.Estimate(returns);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double.IsNaN(result.Covariance[i, j]).Should().BeFalse();
                double.IsInfinity(result.Covariance[i, j]).Should().BeFalse();
            }
        }
    }

    [Fact]
    public void ConditionNumber_IdentityMatrix_IsOne()
    {
        var identity = new double[,]
        {
            { 1.0, 0.0, 0.0 },
            { 0.0, 1.0, 0.0 },
            { 0.0, 0.0, 1.0 },
        };
        var cond = LedoitWolfShrinkage.ConditionNumber(identity);
        cond.Should().BeApproximately(1.0, 1e-10);
    }

    [Fact]
    public void ConditionNumber_SingularMatrix_IsInfinite()
    {
        // Symmetric, rank-deficient: row/col 0 == row/col 1, so determinant is 0.
        // Eigenvalues are {2, 1, 0}.
        var singular = new double[,]
        {
            { 1.0, 1.0, 0.0 },
            { 1.0, 1.0, 0.0 },
            { 0.0, 0.0, 1.0 },
        };
        var cond = LedoitWolfShrinkage.ConditionNumber(singular);
        double.IsPositiveInfinity(cond).Should().BeTrue();
    }

    // -------------------- helpers --------------------

    private static double[,] GenerateRandomReturns(int seed, int t, int n, double vol)
    {
        var rng = new Random(seed);
        var returns = new double[t, n];
        for (int i = 0; i < t; i++)
        {
            for (int j = 0; j < n; j++)
            {
                returns[i, j] = NextGaussian(rng) * vol;
            }
        }
        return returns;
    }

    private static double NextGaussian(Random rng)
    {
        // Box-Muller transform.
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
