using System;
using System.Linq;
using FluentAssertions;
using ThatsMyQuant.Application.Analytics;
using Xunit;

namespace ThatsMyQuant.Tests.Analytics;

/// <summary>
/// Unit tests for the Backtest Lab analysis layer (MC fan, linear regression
/// decomposer, Shannon entropy). Each service is independently testable; we
/// fix the random seed where applicable so the dashboard renders identical
/// fan charts across reloads.
/// </summary>
public class BacktestLabAnalysisTests
{
    // ------------------------------------------------------------------ MC Fan

    [Fact]
    public void MonteCarloFan_DeterministicWithSeed()
    {
        var (dates, equity) = SyntheticEquity(n: 100, drift: 0.0008m, vol: 0.01m, seed: 1);
        var svc = new MonteCarloFanService();

        var a = svc.Compute(dates, equity, iterations: 200, randomSeed: 42);
        var b = svc.Compute(dates, equity, iterations: 200, randomSeed: 42);

        a.Percentile50.Should().Equal(b.Percentile50, "same seed must yield identical median path");
        a.Percentile05.Should().Equal(b.Percentile05);
        a.Percentile95.Should().Equal(b.Percentile95);
    }

    [Fact]
    public void MonteCarloFan_PercentilesAreOrdered()
    {
        var (dates, equity) = SyntheticEquity(n: 60, drift: 0.001m, vol: 0.02m, seed: 7);
        var fan = new MonteCarloFanService().Compute(dates, equity, iterations: 500, randomSeed: 7);

        for (int i = 0; i < fan.Percentile50.Length; i++)
        {
            (fan.Percentile05[i] <= fan.Percentile25[i]).Should().BeTrue($"p05 <= p25 at bar {i}");
            (fan.Percentile25[i] <= fan.Percentile50[i]).Should().BeTrue($"p25 <= p50 at bar {i}");
            (fan.Percentile50[i] <= fan.Percentile75[i]).Should().BeTrue($"p50 <= p75 at bar {i}");
            (fan.Percentile75[i] <= fan.Percentile95[i]).Should().BeTrue($"p75 <= p95 at bar {i}");
        }
    }

    [Fact]
    public void MonteCarloFan_FirstBarEqualsRealizedStart()
    {
        // All bootstrap paths start from the same initial capital, so every percentile
        // at bar 0 must equal the realised equity at bar 0.
        var (dates, equity) = SyntheticEquity(n: 30, drift: 0.0005m, vol: 0.01m, seed: 3);
        var fan = new MonteCarloFanService().Compute(dates, equity, iterations: 100, randomSeed: 3);

        fan.Percentile05[0].Should().Be(equity[0]);
        fan.Percentile50[0].Should().Be(equity[0]);
        fan.Percentile95[0].Should().Be(equity[0]);
    }

    [Fact]
    public void MonteCarloFan_DegenerateFlatCurve_ReturnsFlatFan()
    {
        // Flat realised equity (zero returns at every step) → all percentiles are flat.
        var dates = Enumerable.Range(0, 20).Select(i => new DateTime(2026, 1, 1).AddDays(i)).ToArray();
        var equity = Enumerable.Repeat(100_000m, 20).ToArray();

        var fan = new MonteCarloFanService().Compute(dates, equity, iterations: 100, randomSeed: 0);

        fan.Percentile50.Should().AllBeEquivalentTo(100_000m);
        fan.Percentile05.Should().AllBeEquivalentTo(100_000m);
        fan.Percentile95.Should().AllBeEquivalentTo(100_000m);
    }

    [Fact]
    public void MonteCarloFan_ThrowsOnLengthMismatch()
    {
        var dates = new[] { DateTime.UtcNow, DateTime.UtcNow.AddDays(1) };
        var equity = new[] { 1m, 2m, 3m };

        var act = () => new MonteCarloFanService().Compute(dates, equity);
        act.Should().Throw<ArgumentException>().WithMessage("*align*");
    }

    [Fact]
    public void MonteCarloFan_RealizedCurveEchoedBack()
    {
        // The result preserves the input curve so the React chart can overlay it
        // without re-passing it as a separate prop.
        var (dates, equity) = SyntheticEquity(n: 50, drift: 0.0002m, vol: 0.005m, seed: 9);
        var fan = new MonteCarloFanService().Compute(dates, equity, iterations: 100, randomSeed: 9);

        fan.RealizedEquity.Should().Equal(equity);
        fan.Dates.Should().Equal(dates);
    }

    // -------------------------------------------------------- Linear Regression

    [Fact]
    public void LinearRegression_PerfectLine_RSquaredEqualsOne()
    {
        // y = 2x + 100. R² should be exactly 1, residuals zero.
        var equity = Enumerable.Range(0, 50).Select(i => 100m + 2m * i).ToArray();
        var result = new LinearRegressionDecomposer().Decompose(equity);

        result.SlopeUsdPerBar.Should().BeApproximately(2.0, 1e-9);
        result.InterceptUsd.Should().BeApproximately(100.0, 1e-9);
        result.RSquared.Should().BeApproximately(1.0, 1e-9);
        result.ResidualsUsd.Should().AllSatisfy(r => Math.Abs((double)r).Should().BeLessThan(1e-6));
    }

    [Fact]
    public void LinearRegression_FlatLine_SlopeZero()
    {
        var equity = Enumerable.Repeat(50_000m, 30).ToArray();
        var result = new LinearRegressionDecomposer().Decompose(equity);

        result.SlopeUsdPerBar.Should().BeApproximately(0.0, 1e-9);
        result.InterceptUsd.Should().BeApproximately(50_000.0, 1e-9);
        result.RSquared.Should().Be(0.0, "no variance to explain on a flat line");
    }

    [Fact]
    public void LinearRegression_NoisyLine_RSquaredBetween0And1()
    {
        var (_, equity) = SyntheticEquity(n: 200, drift: 0.0008m, vol: 0.02m, seed: 5);
        var result = new LinearRegressionDecomposer().Decompose(equity);

        result.RSquared.Should().BeInRange(0.0, 1.0);
        result.NObservations.Should().Be(200);
        result.TrendUsd.Length.Should().Be(200);
        result.ResidualsUsd.Length.Should().Be(200);
    }

    [Fact]
    public void LinearRegression_TrendPlusResiduals_EqualsRealized()
    {
        var (_, equity) = SyntheticEquity(n: 100, drift: 0.001m, vol: 0.01m, seed: 11);
        var result = new LinearRegressionDecomposer().Decompose(equity);

        for (int i = 0; i < equity.Length; i++)
        {
            (result.TrendUsd[i] + result.ResidualsUsd[i]).Should()
                .BeApproximately(equity[i], 1e-4m, $"trend+resid must reconstruct realised at bar {i}");
        }
    }

    [Fact]
    public void LinearRegression_DegenerateInputs()
    {
        // Empty curve → all-zero result, no exception.
        var empty = new LinearRegressionDecomposer().Decompose(Array.Empty<decimal>());
        empty.NObservations.Should().Be(0);
        empty.TrendUsd.Should().BeEmpty();

        // Single-point curve → flat trend at that value.
        var single = new LinearRegressionDecomposer().Decompose(new[] { 12345m });
        single.NObservations.Should().Be(1);
        single.TrendUsd.Should().Equal(new[] { 12345m });
        single.SlopeUsdPerBar.Should().Be(0.0);
    }

    // ----------------------------------------------------------- Shannon Entropy

    [Fact]
    public void ShannonEntropy_FlatReturns_EntropyZero()
    {
        // Constant equity ⇒ all-zero returns ⇒ all returns in one bin ⇒ H=0.
        var dates = Enumerable.Range(0, 50).Select(i => new DateTime(2026, 1, 1).AddDays(i)).ToArray();
        var equity = Enumerable.Repeat(10_000m, 50).ToArray();
        var result = new ShannonEntropyAnalyzer().Analyse(dates, equity, window: 30, bins: 10);

        // First 30 entries are NaN (window not yet full); the rest must be 0.
        for (int i = 30; i < 50; i++)
        {
            result.Entropy[i].Should().Be(0.0, $"flat returns ⇒ H=0 at bar {i}");
        }
    }

    [Fact]
    public void ShannonEntropy_UniformReturns_EntropyApproachesMax()
    {
        // Returns spread evenly across all bins should approach ln(bins).
        var dates = Enumerable.Range(0, 100).Select(i => new DateTime(2026, 1, 1).AddDays(i)).ToArray();
        var equity = new decimal[100];
        equity[0] = 1000m;
        // Construct returns that cycle 0..9 evenly to fill 10 bins.
        for (int i = 1; i < 100; i++)
        {
            // Tiny but distinct multiplicative steps so each bar lands in a different bin.
            decimal step = 1m + 0.001m * (i % 10);
            equity[i] = equity[i - 1] * step;
        }

        var result = new ShannonEntropyAnalyzer().Analyse(dates, equity, window: 30, bins: 10);
        var maxH = result.MaxEntropyNats;

        // Last entry should be reasonably close to max — not exactly because the
        // distribution isn't perfectly uniform across the binning window.
        result.Entropy[99].Should().BeGreaterThan(0.6 * maxH);
        result.Entropy[99].Should().BeLessThanOrEqualTo(maxH + 1e-9);
    }

    [Fact]
    public void ShannonEntropy_FirstWindowMinus1AreNaN()
    {
        var dates = Enumerable.Range(0, 50).Select(i => new DateTime(2026, 1, 1).AddDays(i)).ToArray();
        var equity = Enumerable.Range(0, 50).Select(i => 1000m + i * 0.1m).ToArray();
        var result = new ShannonEntropyAnalyzer().Analyse(dates, equity, window: 30, bins: 10);

        for (int i = 0; i < 30; i++)
        {
            double.IsNaN(result.Entropy[i]).Should().BeTrue($"bar {i} should be NaN (window not full)");
        }
        double.IsNaN(result.Entropy[30]).Should().BeFalse("first valid entropy at bar=window");
    }

    [Fact]
    public void ShannonEntropy_TooShortInput_ReturnsAllNaN()
    {
        var dates = Enumerable.Range(0, 5).Select(i => new DateTime(2026, 1, 1).AddDays(i)).ToArray();
        var equity = Enumerable.Range(0, 5).Select(i => 1000m + i).ToArray();
        var result = new ShannonEntropyAnalyzer().Analyse(dates, equity, window: 30, bins: 10);

        result.Entropy.Should().AllSatisfy(v => double.IsNaN(v).Should().BeTrue());
    }

    [Fact]
    public void ShannonEntropy_RejectsBadParameters()
    {
        var dates = new[] { DateTime.UtcNow };
        var equity = new[] { 1m };

        ((Action)(() => new ShannonEntropyAnalyzer().Analyse(dates, equity, window: 4)))
            .Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new ShannonEntropyAnalyzer().Analyse(dates, equity, bins: 1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    // -------------------------------------------------------------- Test fixtures

    /// <summary>
    /// Synthetic equity curve: starts at $100k, applies a mean-drift random walk.
    /// Deterministic given <paramref name="seed"/>.
    /// </summary>
    private static (DateTime[] dates, decimal[] equity) SyntheticEquity(
        int n, decimal drift, decimal vol, int seed)
    {
        var rng = new Random(seed);
        var dates = new DateTime[n];
        var equity = new decimal[n];
        equity[0] = 100_000m;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        dates[0] = t0;
        for (int i = 1; i < n; i++)
        {
            // Box-Muller for a Gaussian shock; cast to decimal for the equity multiply.
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            decimal r = drift + vol * (decimal)z;
            equity[i] = equity[i - 1] * (1m + r);
            dates[i]  = t0.AddHours(i * 4); // 4h bars
        }
        return (dates, equity);
    }
}
