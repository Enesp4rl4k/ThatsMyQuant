using System;
using System.Linq;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class PcaFactorResidualsTests
{
    [Fact]
    public void Decompose_NullReturns_Throws()
    {
        Action act = () => PcaFactorResiduals.Decompose(null!, numFactors: 1);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Decompose_TooFewAssetsOrObservations_Throws()
    {
        var tinyT = new double[1, 5];
        Action shortT = () => PcaFactorResiduals.Decompose(tinyT, numFactors: 1);
        shortT.Should().Throw<ArgumentException>();

        var tinyN = new double[100, 1];
        Action shortN = () => PcaFactorResiduals.Decompose(tinyN, numFactors: 1);
        shortN.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decompose_NumFactorsOutOfRange_Throws()
    {
        var returns = GenerateGaussianReturns(seed: 5, t: 100, n: 4, vol: 0.02);
        Action zero = () => PcaFactorResiduals.Decompose(returns, numFactors: 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();
        Action over = () => PcaFactorResiduals.Decompose(returns, numFactors: 5);
        over.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Decompose_AllAssetsLockstep_FirstFactorExplainsMostVariance()
    {
        // Build returns where every asset is 0.95 × common factor + small noise.
        // First PC should capture nearly all variance.
        var rng = new Random(11);
        int t = 500, n = 5;
        var returns = new double[t, n];
        for (int i = 0; i < t; i++)
        {
            double common = NextGaussian(rng) * 0.02;
            for (int j = 0; j < n; j++)
            {
                double idio = NextGaussian(rng) * 0.001;
                returns[i, j] = 0.95 * common + idio;
            }
        }
        var dec = PcaFactorResiduals.Decompose(returns, numFactors: 1);

        dec.VarianceExplainedFraction.Should().BeGreaterThan(0.95,
            "single common factor should explain >95% of variance for near-lockstep assets");
    }

    [Fact]
    public void Decompose_ResidualMatrixDimensions_Match()
    {
        var returns = GenerateGaussianReturns(seed: 17, t: 200, n: 6, vol: 0.02);
        var dec = PcaFactorResiduals.Decompose(returns, numFactors: 2);

        dec.ResidualMatrix.GetLength(0).Should().Be(200);
        dec.ResidualMatrix.GetLength(1).Should().Be(6);
        dec.FactorLoadings.GetLength(0).Should().Be(6);
        dec.FactorLoadings.GetLength(1).Should().Be(2);
        dec.EigenvaluesUsed.Length.Should().Be(2);
    }

    [Fact]
    public void Decompose_EigenvaluesUsedAreDescending()
    {
        var returns = GenerateGaussianReturns(seed: 23, t: 200, n: 8, vol: 0.02);
        var dec = PcaFactorResiduals.Decompose(returns, numFactors: 4);

        for (int i = 1; i < dec.EigenvaluesUsed.Length; i++)
        {
            dec.EigenvaluesUsed[i - 1].Should().BeGreaterThanOrEqualTo(dec.EigenvaluesUsed[i]);
        }
    }

    [Fact]
    public void ComputeSignals_ProducesOneSignalPerAsset()
    {
        var returns = GenerateGaussianReturns(seed: 29, t: 200, n: 6, vol: 0.02);
        var dec = PcaFactorResiduals.Decompose(returns, numFactors: 2);
        var signals = PcaFactorResiduals.ComputeSignals(dec);

        signals.Should().HaveCount(6);
        signals.Select(s => s.AssetIndex).Distinct().Count().Should().Be(6);
    }

    [Fact]
    public void ComputeSignals_FiniteAndNonNan()
    {
        var returns = GenerateGaussianReturns(seed: 37, t: 200, n: 4, vol: 0.02);
        var dec = PcaFactorResiduals.Decompose(returns, numFactors: 1);
        var signals = PcaFactorResiduals.ComputeSignals(dec);

        foreach (var s in signals)
        {
            double.IsFinite(s.ZScore).Should().BeTrue();
            double.IsFinite(s.LatestResidual).Should().BeTrue();
        }
    }

    private static double[,] GenerateGaussianReturns(int seed, int t, int n, double vol)
    {
        var rng = new Random(seed);
        var ret = new double[t, n];
        for (int i = 0; i < t; i++)
            for (int j = 0; j < n; j++) ret[i, j] = NextGaussian(rng) * vol;
        return ret;
    }

    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

public class CrossQuoteArbitrageTests
{
    [Fact]
    public void Evaluate_InvalidPrice_Throws()
    {
        Action act = () => CrossQuoteArbitrage.Evaluate("BTC", "USDT", "USDC", 0m, 50_000m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Evaluate_BelowThreshold_NoTrigger()
    {
        // 5bp spread, default 15bp threshold → no trigger.
        var eval = CrossQuoteArbitrage.Evaluate(
            "BTC", "USDT", "USDC",
            priceA: 50_025m, priceB: 50_000m);
        eval.Trigger.Should().BeFalse();
        eval.LongLegSide.Should().BeNull();
    }

    [Fact]
    public void Evaluate_QuoteAExpensive_LongQuoteB()
    {
        // priceA 50_080 vs priceB 50_000 → spread ≈ +16bps. Quote A is expensive → long B.
        var eval = CrossQuoteArbitrage.Evaluate(
            "BTC", "USDT", "USDC",
            priceA: 50_080m, priceB: 50_000m);
        eval.Trigger.Should().BeTrue();
        eval.SpreadBps.Should().BeApproximately(16.0, 0.1);
        eval.LongQuote.Should().Be("USDC");
        eval.ShortQuote.Should().Be("USDT");
        eval.LongLegSide.Should().Be(TradeSide.Buy);
        eval.ShortLegSide.Should().Be(TradeSide.Sell);
    }

    [Fact]
    public void Evaluate_QuoteBExpensive_LongQuoteA()
    {
        var eval = CrossQuoteArbitrage.Evaluate(
            "BTC", "USDT", "USDC",
            priceA: 50_000m, priceB: 50_080m);
        eval.Trigger.Should().BeTrue();
        eval.SpreadBps.Should().BeLessThan(0);
        eval.LongQuote.Should().Be("USDT");
        eval.ShortQuote.Should().Be("USDC");
    }

    [Fact]
    public void Evaluate_HigherThreshold_PreventsTrigger()
    {
        // 20bp spread but threshold raised to 50bp → no trigger.
        var eval = CrossQuoteArbitrage.Evaluate(
            "BTC", "USDT", "USDC",
            priceA: 50_100m, priceB: 50_000m,
            entryThresholdBps: 50.0);
        eval.Trigger.Should().BeFalse();
    }

    [Fact]
    public void ComputeSpreadZScore_KnownInput()
    {
        // current = 30, mean = 10, std = 5 → z = 4.0
        double z = CrossQuoteArbitrage.ComputeSpreadZScore(30, 10, 5);
        z.Should().BeApproximately(4.0, 1e-12);
    }

    [Fact]
    public void ComputeSpreadZScore_ZeroStd_ReturnsZero()
    {
        CrossQuoteArbitrage.ComputeSpreadZScore(30, 10, 0).Should().Be(0);
    }
}
