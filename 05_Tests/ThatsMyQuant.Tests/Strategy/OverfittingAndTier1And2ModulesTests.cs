using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

// =============== Overfitting toolkit ===============

public class DeflatedSharpeRatioTests
{
    [Fact]
    public void ProbabilisticSharpe_NullReturns_Throws()
    {
        Action act = () => DeflatedSharpeRatio.ProbabilisticSharpe(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProbabilisticSharpe_TooFew_Throws()
    {
        Action act = () => DeflatedSharpeRatio.ProbabilisticSharpe(new double[] { 0.01, 0.02 });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ProbabilisticSharpe_PositiveSharpe_HighProbability()
    {
        // 200 returns with mean 0.005 stddev 0.01 → SR ≈ 0.5 → very confident > 0.
        var rng = new Random(11);
        var returns = new double[200];
        for (int i = 0; i < returns.Length; i++)
            returns[i] = 0.005 + (rng.NextDouble() - 0.5) * 0.02;
        double psr = DeflatedSharpeRatio.ProbabilisticSharpe(returns);
        psr.Should().BeGreaterThan(0.95, "strong positive Sharpe should produce high PSR");
    }

    [Fact]
    public void Compute_NoTrials_DefaultsToPSR()
    {
        var rng = new Random(13);
        var returns = new double[200];
        for (int i = 0; i < returns.Length; i++)
            returns[i] = 0.005 + (rng.NextDouble() - 0.5) * 0.02;

        var result = DeflatedSharpeRatio.Compute(returns, numTrials: 1, varianceOfTrialSharpes: 0.0);
        result.Threshold.Should().Be(0.0);
        result.DeflatedSharpe.Should().BeApproximately(result.ProbabilisticSharpe, 1e-9);
    }

    [Fact]
    public void Compute_ManyTrials_RaiseThreshold()
    {
        var rng = new Random(17);
        var returns = new double[200];
        for (int i = 0; i < returns.Length; i++)
            returns[i] = 0.003 + (rng.NextDouble() - 0.5) * 0.02;

        var noTrials = DeflatedSharpeRatio.Compute(returns, numTrials: 1, 0.0);
        var manyTrials = DeflatedSharpeRatio.Compute(returns, numTrials: 100, varianceOfTrialSharpes: 0.5);

        manyTrials.Threshold.Should().BeGreaterThan(noTrials.Threshold);
        manyTrials.DeflatedSharpe.Should().BeLessThan(manyTrials.ProbabilisticSharpe,
            "selection bias correction lowers DSR vs PSR");
    }

    [Fact]
    public void Compute_NegativeSkew_LowersPSR()
    {
        // Generate positively skewed and negatively skewed series with same mean/std
        // (squared returns vs negated squared returns).
        var rng = new Random(23);
        var negSkew = new double[200];
        for (int i = 0; i < 200; i++)
        {
            double u = rng.NextDouble();
            // Heavy left tail.
            negSkew[i] = u < 0.05 ? -0.05 : 0.005;
        }
        double psr = DeflatedSharpeRatio.ProbabilisticSharpe(negSkew);
        psr.Should().BeLessThan(1.0);  // should not be 100% confident given the negative skew
    }
}

public class PurgedKFoldSplitterTests
{
    [Fact]
    public void Generate_TooFewSamples_Throws()
    {
        Action act = () => PurgedKFoldSplitter.Generate(numSamples: 5, numFolds: 5);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Generate_ProducesKFolds_CoverAllSamples()
    {
        var splits = PurgedKFoldSplitter.Generate(numSamples: 100, numFolds: 5);
        splits.Should().HaveCount(5);

        var allTest = new HashSet<int>();
        foreach (var s in splits)
            foreach (var i in s.TestIndices) allTest.Add(i);

        allTest.Count.Should().Be(100);
    }

    [Fact]
    public void Generate_NoOverlap_BetweenTrainAndTest()
    {
        var splits = PurgedKFoldSplitter.Generate(numSamples: 100, numFolds: 4);
        foreach (var s in splits)
        {
            var testSet = new HashSet<int>(s.TestIndices);
            foreach (var trainIdx in s.TrainIndices)
                testSet.Should().NotContain(trainIdx);
        }
    }

    [Fact]
    public void Generate_Purge_RemovesSamplesNearTestBoundary()
    {
        var withoutPurge = PurgedKFoldSplitter.Generate(numSamples: 100, numFolds: 5, purgeBars: 0);
        var withPurge = PurgedKFoldSplitter.Generate(numSamples: 100, numFolds: 5, purgeBars: 3);

        withPurge[2].TrainIndices.Count.Should().BeLessThan(withoutPurge[2].TrainIndices.Count);
    }

    [Fact]
    public void ComputePbo_AllPicksWin_ReturnsZero()
    {
        // IS leaderboard EXACTLY matches OOS leaderboard.
        var inSample = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        var oos = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        double pbo = PurgedKFoldSplitter.ComputePbo(inSample, oos);
        pbo.Should().Be(0.0);
    }

    [Fact]
    public void ComputePbo_AllPicksLose_ReturnsOne()
    {
        // IS-best is OOS-worst.
        var inSample = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        var oos = new[] { 5.0, 4.0, 3.0, 2.0, 1.0 };
        double pbo = PurgedKFoldSplitter.ComputePbo(inSample, oos);
        pbo.Should().Be(1.0);
    }

    [Fact]
    public void ComputePbo_LengthMismatch_Throws()
    {
        Action act = () => PurgedKFoldSplitter.ComputePbo(new[] { 1.0 }, new[] { 1.0, 2.0 });
        act.Should().Throw<ArgumentException>();
    }
}

public class BlockBootstrapSharpeTests
{
    [Fact]
    public void Compute_NullReturns_Throws()
    {
        Action act = () => BlockBootstrapSharpe.Compute(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Compute_PositiveSharpeSeries_ProducesCiAroundEstimate()
    {
        var rng = new Random(31);
        var returns = new double[200];
        for (int i = 0; i < returns.Length; i++) returns[i] = 0.005 + (rng.NextDouble() - 0.5) * 0.02;

        var ci = BlockBootstrapSharpe.Compute(returns, numBootstrap: 500, seed: 7);
        ci.LowerBound.Should().BeLessThan(ci.UpperBound);
        ci.PointEstimate.Should().BeInRange(ci.LowerBound - 0.5, ci.UpperBound + 0.5);
    }

    [Fact]
    public void Compute_BadPercentiles_Throws()
    {
        var returns = new double[100];
        for (int i = 0; i < 100; i++) returns[i] = 0.001;
        Action act = () => BlockBootstrapSharpe.Compute(returns,
            lowerPercentile: 0.5, upperPercentile: 0.3);
        act.Should().Throw<ArgumentException>();
    }
}

// =============== Tier 1 modules ===============

public class VolatilityCarryDetectorTests
{
    [Fact]
    public void Evaluate_HighIvLowRv_ShortVolSignal()
    {
        var e = VolatilityCarryDetector.Evaluate(impliedVolAnnualized: 0.80, realizedVolAnnualized: 0.50);
        e.Direction.Should().Be(VolatilityCarryDetector.CarryDirection.ShortVol);
        e.VrpFraction.Should().BeGreaterThan(0);
        e.EdgeBps.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Evaluate_BalancedIvRv_NoSignal()
    {
        var e = VolatilityCarryDetector.Evaluate(0.50, 0.50);
        e.Direction.Should().Be(VolatilityCarryDetector.CarryDirection.None);
        e.EdgeBps.Should().Be(0);
    }

    [Fact]
    public void Evaluate_RvFarAboveIv_LongVolSignal()
    {
        var e = VolatilityCarryDetector.Evaluate(impliedVolAnnualized: 0.50, realizedVolAnnualized: 0.70);
        e.Direction.Should().Be(VolatilityCarryDetector.CarryDirection.LongVol);
    }

    [Fact]
    public void Evaluate_InvalidVol_Throws()
    {
        Action act = () => VolatilityCarryDetector.Evaluate(0, 0.5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ComputeRealizedVolatility_KnownDailySeries_Annualizes()
    {
        // 252 daily returns with std ~0.01 → annualized ~0.01·√252 ~ 0.1587
        var rng = new Random(41);
        var ret = new double[252];
        for (int i = 0; i < 252; i++) ret[i] = (rng.NextDouble() - 0.5) * 0.034;  // std ≈ 0.01
        double rv = VolatilityCarryDetector.ComputeRealizedVolatility(ret, periodsPerYear: 252);
        rv.Should().BeInRange(0.10, 0.25);
    }

    [Fact]
    public void EvaluatePutSkew_HighPutIv_FiresSell()
    {
        var (sell, skew) = VolatilityCarryDetector.EvaluatePutSkew(put25DeltaIv: 0.70, call25DeltaIv: 0.55);
        sell.Should().BeTrue();
        skew.Should().BeGreaterThan(0.05);
    }
}

public class OnChainSignalAggregatorTests
{
    [Fact]
    public void Aggregate_NullSnapshot_Throws()
    {
        Action act = () => OnChainSignalAggregator.Aggregate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Aggregate_StableInflowDominant_BullishBias()
    {
        var snap = new OnChainSignalAggregator.OnChainSnapshot(
            StablecoinNetExchangeInflowUsd: 500_000_000,
            BitcoinNetExchangeInflowBtc: 100,
            WhaleTransferCountLast24h: 60,
            MinerOutflowBtc: 200);
        var sig = OnChainSignalAggregator.Aggregate(snap);
        sig.DirectionalBias.Should().BeGreaterThan(0, "stablecoin inflow dominant → bullish");
    }

    [Fact]
    public void Aggregate_BtcInflowAndMinerOutflow_BearishBias()
    {
        var snap = new OnChainSignalAggregator.OnChainSnapshot(
            StablecoinNetExchangeInflowUsd: 50_000_000,
            BitcoinNetExchangeInflowBtc: 20_000,
            WhaleTransferCountLast24h: 100,
            MinerOutflowBtc: 5000);
        var sig = OnChainSignalAggregator.Aggregate(snap);
        sig.DirectionalBias.Should().BeLessThan(0);
    }

    [Fact]
    public void AsSide_LowConfidence_ReturnsNull()
    {
        var snap = new OnChainSignalAggregator.OnChainSnapshot(0, 0, 0, 0);
        var sig = OnChainSignalAggregator.Aggregate(snap);
        var side = OnChainSignalAggregator.AsSide(sig);
        side.Should().BeNull();
    }

    [Fact]
    public void AsSide_HighBullishBias_ReturnsBuy()
    {
        var snap = new OnChainSignalAggregator.OnChainSnapshot(
            StablecoinNetExchangeInflowUsd: 1_000_000_000,
            BitcoinNetExchangeInflowBtc: 0,
            WhaleTransferCountLast24h: 200,
            MinerOutflowBtc: 0);
        var sig = OnChainSignalAggregator.Aggregate(snap);
        var side = OnChainSignalAggregator.AsSide(sig);
        side.Should().Be(TradeSide.Buy);
    }
}

public class CrossExchangeArbitrageTests
{
    [Fact]
    public void Evaluate_BelowEdgeThreshold_NoTrigger()
    {
        var e = CrossExchangeArbitrage.Evaluate(
            "BTC-USDT-SWAP", "OKX", "Binance",
            priceA: 50_005m, priceB: 50_000m,   // 1bp spread, 6bp fees → no edge
            venueAFeeRoundTripBps: 6.0, venueBFeeRoundTripBps: 6.0);
        e.Trigger.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_OkxExpensive_BuyBinanceSellOkx()
    {
        // 30bp spread - 6bp fees = 24bp edge, well above default 5bp threshold.
        var e = CrossExchangeArbitrage.Evaluate(
            "BTC-USDT-SWAP", "OKX", "Binance",
            priceA: 50_150m, priceB: 50_000m);
        e.Trigger.Should().BeTrue();
        e.SellVenue.Should().Be("OKX");
        e.BuyVenue.Should().Be("Binance");
    }

    [Fact]
    public void Evaluate_BinanceExpensive_BuyOkxSellBinance()
    {
        var e = CrossExchangeArbitrage.Evaluate(
            "BTC-USDT-SWAP", "OKX", "Binance",
            priceA: 50_000m, priceB: 50_150m);
        e.Trigger.Should().BeTrue();
        e.SellVenue.Should().Be("Binance");
        e.BuyVenue.Should().Be("OKX");
    }

    [Fact]
    public void ShouldExit_SignFlip_ReturnsTrue()
    {
        bool exit = CrossExchangeArbitrage.ShouldExit(currentSpreadBps: -1.0, entrySpreadBps: 30.0);
        exit.Should().BeTrue();
    }

    [Fact]
    public void ShouldExit_HalfwayConverged_ReturnsTrue()
    {
        bool exit = CrossExchangeArbitrage.ShouldExit(
            currentSpreadBps: 14.0, entrySpreadBps: 30.0, exitFraction: 0.5);
        exit.Should().BeTrue();
    }
}

// =============== Tier 2 modules ===============

public class MarkovRegimeSwitchingTests
{
    [Fact]
    public void Fit_TooFewObservations_Throws()
    {
        Action act = () => MarkovRegimeSwitching.Fit(new double[] { 0.01, 0.02 });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Fit_RecoversTwoRegimes_FromTrueRegimeData()
    {
        // Generate data from a known 2-state regime: low-vol vs high-vol.
        var rng = new Random(7);
        int t = 400;
        var ret = new double[t];
        for (int i = 0; i < t; i++)
        {
            // Regime 0 (low vol): blocks of length ~50, std 0.005. Regime 1 (high vol): std 0.03.
            int regime = (i / 50) % 2;
            double sigma = regime == 0 ? 0.005 : 0.03;
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            ret[i] = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2) * sigma;
        }
        var fit = MarkovRegimeSwitching.Fit(ret, maxIterations: 100);

        // The two states should have meaningfully different sigma.
        double ratio = fit.Sigma[1] / fit.Sigma[0];
        ratio.Should().BeGreaterThan(2.0, "high-vol state must be at least 2× the low-vol one");
    }

    [Fact]
    public void Fit_TransitionMatrix_RowsSumToOne()
    {
        var rng = new Random(11);
        var ret = new double[200];
        for (int i = 0; i < 200; i++) ret[i] = (rng.NextDouble() - 0.5) * 0.02;
        var fit = MarkovRegimeSwitching.Fit(ret);

        for (int i = 0; i < 2; i++)
        {
            double sum = 0;
            for (int j = 0; j < 2; j++) sum += fit.TransitionMatrix[i, j];
            sum.Should().BeApproximately(1.0, 1e-6);
        }
    }

    [Fact]
    public void Forecast_RegimeProbs_SumToOne()
    {
        var rng = new Random(13);
        var ret = new double[200];
        for (int i = 0; i < 200; i++) ret[i] = (rng.NextDouble() - 0.5) * 0.02;
        var fit = MarkovRegimeSwitching.Fit(ret);
        var fc = MarkovRegimeSwitching.Forecast(fit);

        double sum = fc.CurrentRegimeProbs.Sum();
        sum.Should().BeApproximately(1.0, 1e-6);
        fc.MostLikelyRegime.Should().BeOneOf(0, 1);
    }
}

public class DccGarchCorrelationTests
{
    [Fact]
    public void Fit_TooFewAssets_Throws()
    {
        var bad = new double[100, 1];
        Action act = () => DccGarchCorrelation.Fit(bad);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Fit_Returns_AlphaPlusBetaBelowOne()
    {
        var rng = new Random(43);
        int t = 200, n = 3;
        var ret = new double[t, n];
        for (int i = 0; i < t; i++)
            for (int j = 0; j < n; j++)
            {
                double u = 1.0 - rng.NextDouble();
                double v = 1.0 - rng.NextDouble();
                ret[i, j] = Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v) * 0.02;
            }
        var fit = DccGarchCorrelation.Fit(ret);
        (fit.Alpha + fit.Beta).Should().BeLessThan(1.0);
        fit.UnivariateGarch.Length.Should().Be(n);
    }

    [Fact]
    public void DynamicCorrelation_OnDiagonalIsOne_OffDiagonalInRange()
    {
        var rng = new Random(47);
        int t = 150, n = 3;
        var ret = new double[t, n];
        for (int i = 0; i < t; i++)
            for (int j = 0; j < n; j++)
            {
                double u = 1.0 - rng.NextDouble();
                double v = 1.0 - rng.NextDouble();
                ret[i, j] = Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v) * 0.02;
            }
        var fit = DccGarchCorrelation.Fit(ret);
        for (int tt = 0; tt < t; tt++)
        {
            for (int i = 0; i < n; i++)
            {
                fit.DynamicCorrelationMatrices[tt, i, i].Should().BeApproximately(1.0, 1e-6);
                for (int j = 0; j < n; j++)
                {
                    fit.DynamicCorrelationMatrices[tt, i, j].Should().BeInRange(-1.0, 1.0);
                }
            }
        }
    }

    [Fact]
    public void ForecastNextCorrelation_DimensionsMatch()
    {
        var rng = new Random(53);
        int t = 100, n = 3;
        var ret = new double[t, n];
        for (int i = 0; i < t; i++)
            for (int j = 0; j < n; j++) ret[i, j] = (rng.NextDouble() - 0.5) * 0.02;
        var fit = DccGarchCorrelation.Fit(ret);

        var lastEps = new double[] { 0.5, -0.3, 0.1 };
        var Rnext = DccGarchCorrelation.ForecastNextCorrelation(fit, lastEps);
        Rnext.GetLength(0).Should().Be(n);
        Rnext.GetLength(1).Should().Be(n);
        for (int i = 0; i < n; i++) Rnext[i, i].Should().BeApproximately(1.0, 1e-6);
    }
}

public class AlmgrenChrissExecutionTests
{
    [Fact]
    public void ComputeSchedule_SumOfVolumeFractionsIsOne()
    {
        var plan = AlmgrenChrissExecution.ComputeSchedule(totalSlices: 10);
        double sum = plan.Slices.Sum(s => s.VolumeFraction);
        sum.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void ComputeSchedule_RiskNeutral_DegeneratesToTwap()
    {
        var plan = AlmgrenChrissExecution.ComputeSchedule(
            totalSlices: 10, riskAversion: 0);
        // Each slice should be exactly 0.10 of total when TWAP.
        foreach (var slice in plan.Slices)
        {
            slice.VolumeFraction.Should().BeApproximately(0.1, 1e-6);
        }
    }

    [Fact]
    public void ComputeSchedule_HighRiskAversion_FrontLoaded()
    {
        var twap = AlmgrenChrissExecution.ComputeSchedule(
            totalSlices: 10, riskAversion: 0);
        var aggressive = AlmgrenChrissExecution.ComputeSchedule(
            totalSlices: 10, riskAversion: 1e-2);

        // Aggressive (high-λ) front-loads volume — first slice should be larger than TWAP.
        aggressive.Slices[0].VolumeFraction.Should().BeGreaterThan(twap.Slices[0].VolumeFraction);
    }

    [Fact]
    public void ScheduleToNotional_ScalesByTarget()
    {
        var plan = AlmgrenChrissExecution.ComputeSchedule(totalSlices: 5);
        var notionals = AlmgrenChrissExecution.ScheduleToNotional(plan, targetNotional: 10_000m);
        notionals.Sum().Should().BeApproximately(10_000m, 0.01m);
    }

    [Fact]
    public void ComputeSchedule_InvalidParams_Throws()
    {
        Action zeroSlices = () => AlmgrenChrissExecution.ComputeSchedule(totalSlices: 0);
        zeroSlices.Should().Throw<ArgumentOutOfRangeException>();
        Action zeroT = () => AlmgrenChrissExecution.ComputeSchedule(totalTimeBars: 0);
        zeroT.Should().Throw<ArgumentOutOfRangeException>();
    }
}
