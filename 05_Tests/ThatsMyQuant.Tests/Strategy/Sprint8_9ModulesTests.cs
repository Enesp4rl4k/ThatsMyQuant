using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class InformationCoefficientAggregatorTests
{
    [Fact]
    public void Aggregate_NullSignals_Throws()
    {
        Action act = () => InformationCoefficientAggregator.Aggregate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Aggregate_NoSignals_ReturnsZero()
    {
        var result = InformationCoefficientAggregator.Aggregate(
            Array.Empty<InformationCoefficientAggregator.SignalEntry>());
        result.CompositeScore.Should().Be(0.0);
        result.EffectiveWeight.Should().Be(0.0);
    }

    [Fact]
    public void Aggregate_SingleStrongSignal_PreservesValue()
    {
        var signals = new[]
        {
            new InformationCoefficientAggregator.SignalEntry("A", Value: 2.5, HistoricalIc: 0.10)
        };
        var result = InformationCoefficientAggregator.Aggregate(signals, exponent: 1.0);

        result.CompositeScore.Should().BeApproximately(2.5, 1e-9);
    }

    [Fact]
    public void Aggregate_NegativeIc_FlipsContributionSign()
    {
        // A negative IC means the signal predicts the OPPOSITE direction.
        // Composite should flip sign accordingly.
        var signals = new[]
        {
            new InformationCoefficientAggregator.SignalEntry("A", Value: 2.0, HistoricalIc: -0.10)
        };
        var result = InformationCoefficientAggregator.Aggregate(signals);
        result.CompositeScore.Should().Be(-2.0);
    }

    [Fact]
    public void Aggregate_WeakSignalDropped_BelowMinIc()
    {
        var signals = new[]
        {
            new InformationCoefficientAggregator.SignalEntry("Strong", 1.0, 0.10),
            new InformationCoefficientAggregator.SignalEntry("Weak", 5.0, 0.005)
        };
        var result = InformationCoefficientAggregator.Aggregate(signals, minAbsIc: 0.02);

        // The weak signal should contribute 0 (below threshold). Composite ≈ 1.0 (only strong).
        result.CompositeScore.Should().BeApproximately(1.0, 1e-9);
        result.ContributingSignals.First(s => s.Name == "Weak").WeightApplied.Should().Be(0);
    }

    [Fact]
    public void Aggregate_MultipleSignalsLinearWeighting()
    {
        // Two equal signals, IC 0.10 and 0.05. Linear weight (p=1):
        // composite = (0.10·2 + 0.05·1) / (0.10 + 0.05) = 0.25 / 0.15 = 1.667
        var signals = new[]
        {
            new InformationCoefficientAggregator.SignalEntry("A", 2.0, 0.10),
            new InformationCoefficientAggregator.SignalEntry("B", 1.0, 0.05)
        };
        var result = InformationCoefficientAggregator.Aggregate(signals, exponent: 1.0);
        result.CompositeScore.Should().BeApproximately(0.25 / 0.15, 1e-9);
    }

    [Fact]
    public void Aggregate_QuadraticExponent_EmphasizesStrongerIc()
    {
        var signals = new[]
        {
            new InformationCoefficientAggregator.SignalEntry("Big", 1.0, 0.20),
            new InformationCoefficientAggregator.SignalEntry("Small", -1.0, 0.05)
        };
        var p1 = InformationCoefficientAggregator.Aggregate(signals, exponent: 1.0);
        var p2 = InformationCoefficientAggregator.Aggregate(signals, exponent: 2.0);

        // Both push composite positive (Big dominates), but quadratic should push it MORE positive.
        p2.CompositeScore.Should().BeGreaterThan(p1.CompositeScore);
    }

    [Fact]
    public void SpearmanRank_PerfectMonotonic_ReturnsOne()
    {
        var x = new double[] { 1, 2, 3, 4, 5 };
        var y = new double[] { 10, 20, 30, 40, 50 };
        InformationCoefficientAggregator.SpearmanRank(x, y).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void SpearmanRank_AntiMonotonic_ReturnsMinusOne()
    {
        var x = new double[] { 1, 2, 3, 4, 5 };
        var y = new double[] { 50, 40, 30, 20, 10 };
        InformationCoefficientAggregator.SpearmanRank(x, y).Should().BeApproximately(-1.0, 1e-9);
    }

    [Fact]
    public void SpearmanRank_LengthMismatch_Throws()
    {
        Action act = () => InformationCoefficientAggregator.SpearmanRank(
            new double[] { 1, 2 }, new double[] { 1, 2, 3 });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Standardize_KnownInput_HasZeroMeanUnitStd()
    {
        var values = new double[] { 1, 2, 3, 4, 5 };
        var z = InformationCoefficientAggregator.Standardize(values);

        double mean = z.Average();
        double std = Math.Sqrt(z.Sum(v => (v - mean) * (v - mean)) / (z.Length - 1));
        mean.Should().BeApproximately(0.0, 1e-9);
        std.Should().BeApproximately(1.0, 1e-9);
    }
}

public class Garch11VolatilityTests
{
    [Fact]
    public void Fit_NullReturns_Throws()
    {
        Action act = () => Garch11Volatility.Fit(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Fit_TooFewObservations_Throws()
    {
        var r = new double[] { 0.01, -0.02, 0.005 };
        Action act = () => Garch11Volatility.Fit(r);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Fit_StationarityConstraint_AlphaPlusBetaBelowOne()
    {
        var rng = new Random(13);
        int t = 500;
        var r = new double[t];
        for (int i = 0; i < t; i++)
        {
            double u = 1.0 - rng.NextDouble();
            double v = 1.0 - rng.NextDouble();
            r[i] = Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v) * 0.02;
        }
        var fit = Garch11Volatility.Fit(r);
        (fit.Alpha + fit.Beta).Should().BeLessThan(1.0);
        fit.Omega.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Fit_VolatilityClustered_RecoversHighPersistence()
    {
        // Simulate GARCH-like clustered returns: vol regimes alternating.
        var rng = new Random(31);
        int t = 600;
        var r = new double[t];
        double vol = 0.02;
        for (int i = 0; i < t; i++)
        {
            // Volatility regime switches every 100 bars.
            vol = (i / 100) % 2 == 0 ? 0.005 : 0.05;
            double u = 1.0 - rng.NextDouble();
            double v = 1.0 - rng.NextDouble();
            r[i] = Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v) * vol;
        }
        var fit = Garch11Volatility.Fit(r);
        // For clustered series, persistence (β) should be substantial.
        fit.Beta.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void ForecastNextVariance_UsesGarchRecursion()
    {
        var fit = new Garch11Volatility.FitResult(
            Omega: 1e-6, Alpha: 0.10, Beta: 0.85, LogLikelihood: 0, UnconditionalVariance: 2e-5);

        double next = Garch11Volatility.ForecastNextVariance(
            lastReturn: 0.05, lastVariance: 1e-4, fit);

        // Manual: 1e-6 + 0.10·(0.05)² + 0.85·1e-4 = 1e-6 + 2.5e-4 + 8.5e-5 = 3.36e-4
        next.Should().BeApproximately(1e-6 + 0.10 * 0.0025 + 0.85 * 1e-4, 1e-12);
    }

    [Fact]
    public void ConditionalVariances_LengthMatchesInput()
    {
        var rng = new Random(41);
        int t = 100;
        var r = new double[t];
        for (int i = 0; i < t; i++) r[i] = (rng.NextDouble() - 0.5) * 0.04;

        var fit = new Garch11Volatility.FitResult(1e-6, 0.10, 0.85, 0, 2e-5);
        var sigma2 = Garch11Volatility.ConditionalVariances(r, fit);
        sigma2.Length.Should().Be(t);
        for (int i = 0; i < t; i++) sigma2[i].Should().BeGreaterThan(0);
    }
}
