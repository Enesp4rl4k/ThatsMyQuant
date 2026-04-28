using System;
using System.Collections.Generic;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class BtcDominanceOverlayTests
{
    [Fact]
    public void Compute_NullHistory_Throws()
    {
        Action act = () => BtcDominanceOverlay.Compute(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Compute_InsufficientData_Throws()
    {
        var history = new double[] { 0.50, 0.51 };
        Action act = () => BtcDominanceOverlay.Compute(history, lookbackDays: 30);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Compute_DominanceRising_AltMultiplierBelowOne()
    {
        // BTC.D rising from 0.50 to 0.54 over 30 days = +8% momentum.
        var history = new List<double>();
        for (int i = 0; i < 31; i++)
            history.Add(0.50 + (0.04 * i / 30));
        var result = BtcDominanceOverlay.Compute(history, lookbackDays: 30, alpha: 5.0);

        result.DominanceMomentum.Should().BeApproximately(0.08, 0.001);
        result.AltMultiplier.Should().BeLessThan(1.0, "rising dominance reduces alt sizing");
        result.BtcMultiplier.Should().BeGreaterThan(1.0, "rising dominance increases BTC sizing");
    }

    [Fact]
    public void Compute_DominanceFalling_AltMultiplierAboveOne()
    {
        var history = new List<double>();
        for (int i = 0; i < 31; i++)
            history.Add(0.54 - (0.04 * i / 30));
        var result = BtcDominanceOverlay.Compute(history, lookbackDays: 30, alpha: 5.0);

        result.DominanceMomentum.Should().BeLessThan(0);
        result.AltMultiplier.Should().BeGreaterThan(1.0);
        result.BtcMultiplier.Should().BeLessThan(1.0);
    }

    [Fact]
    public void Compute_RespectsClampBounds()
    {
        // Extreme momentum should be clipped to floor / ceiling.
        var history = new List<double>();
        for (int i = 0; i < 31; i++) history.Add(0.30 + 0.30 * i / 30);  // doubles in 30 days
        var result = BtcDominanceOverlay.Compute(
            history, lookbackDays: 30, alpha: 50.0,
            multiplierFloor: 0.4, multiplierCeiling: 1.6);

        result.AltMultiplier.Should().Be(0.4);
        result.BtcMultiplier.Should().Be(1.6);
    }

    [Fact]
    public void ScaleWeight_AltUsesAltMultiplier()
    {
        var overlay = new BtcDominanceOverlay.DominanceOverlayResult(
            DominanceMomentum: 0.05, AltMultiplier: 0.75, BtcMultiplier: 1.25,
            DominanceNow: 0.52, DominanceLookback: 0.50);
        BtcDominanceOverlay.ScaleWeight(overlay, isBtc: true).Should().Be(1.25);
        BtcDominanceOverlay.ScaleWeight(overlay, isBtc: false).Should().Be(0.75);
    }
}

public class LiquidationCascadeFaderTests
{
    [Fact]
    public void Detect_NullEvents_Throws()
    {
        Action act = () => LiquidationCascadeFader.Detect(null!, "BTC-USDT-SWAP", DateTime.UtcNow, 5_000_000m);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Detect_BelowTrigger_ReturnsNull()
    {
        var t = DateTime.UtcNow;
        var events = new List<LiquidationCascadeFader.LiquidationEvent>
        {
            new(t.AddMinutes(-1), "BTC-USDT-SWAP", TradeSide.Buy, 1_000_000m)
        };
        var result = LiquidationCascadeFader.Detect(events, "BTC-USDT-SWAP", t, triggerNotional: 5_000_000m);
        result.Should().BeNull();
    }

    [Fact]
    public void Detect_LongCascade_ReturnsBuyFade()
    {
        // 6M of long liquidations in 5 min, dominantly long → fade BUY.
        var t = DateTime.UtcNow;
        var events = new List<LiquidationCascadeFader.LiquidationEvent>
        {
            new(t.AddMinutes(-3), "BTC-USDT-SWAP", TradeSide.Buy, 3_000_000m),
            new(t.AddMinutes(-2), "BTC-USDT-SWAP", TradeSide.Buy, 2_500_000m),
            new(t.AddMinutes(-1), "BTC-USDT-SWAP", TradeSide.Sell, 500_000m),
        };
        var result = LiquidationCascadeFader.Detect(
            events, "BTC-USDT-SWAP", t, triggerNotional: 5_000_000m);

        result.Should().NotBeNull();
        result!.DominantSide.Should().Be(TradeSide.Buy);
        result.FadeSide.Should().Be(TradeSide.Buy, "fade buys the dip from a long cascade");
        result.LongSideNotional.Should().Be(5_500_000m);
        result.ShortSideNotional.Should().Be(500_000m);
        result.StrengthScore.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Detect_ShortCascade_ReturnsSellFade()
    {
        var t = DateTime.UtcNow;
        var events = new List<LiquidationCascadeFader.LiquidationEvent>
        {
            new(t.AddMinutes(-2), "BTC-USDT-SWAP", TradeSide.Sell, 7_000_000m)
        };
        var result = LiquidationCascadeFader.Detect(events, "BTC-USDT-SWAP", t, triggerNotional: 5_000_000m);
        result.Should().NotBeNull();
        result!.DominantSide.Should().Be(TradeSide.Sell);
        result.FadeSide.Should().Be(TradeSide.Sell);
    }

    [Fact]
    public void Detect_MixedDominance_ReturnsNull()
    {
        // 5.5M long + 5M short — no clear side dominance → don't fade.
        var t = DateTime.UtcNow;
        var events = new List<LiquidationCascadeFader.LiquidationEvent>
        {
            new(t.AddMinutes(-2), "BTC-USDT-SWAP", TradeSide.Buy, 5_500_000m),
            new(t.AddMinutes(-2), "BTC-USDT-SWAP", TradeSide.Sell, 5_000_000m)
        };
        var result = LiquidationCascadeFader.Detect(events, "BTC-USDT-SWAP", t, triggerNotional: 5_000_000m);
        result.Should().BeNull();
    }

    [Fact]
    public void Detect_OutOfWindow_Excluded()
    {
        var t = DateTime.UtcNow;
        var events = new List<LiquidationCascadeFader.LiquidationEvent>
        {
            // 30 minutes ago — outside the 5 min window.
            new(t.AddMinutes(-30), "BTC-USDT-SWAP", TradeSide.Buy, 6_000_000m)
        };
        var result = LiquidationCascadeFader.Detect(events, "BTC-USDT-SWAP", t, triggerNotional: 5_000_000m);
        result.Should().BeNull();
    }

    [Fact]
    public void Detect_StrengthScoreSaturates()
    {
        var t = DateTime.UtcNow;
        var events = new List<LiquidationCascadeFader.LiquidationEvent>
        {
            new(t.AddMinutes(-1), "BTC-USDT-SWAP", TradeSide.Buy, 100_000_000m)  // 20× trigger
        };
        var result = LiquidationCascadeFader.Detect(events, "BTC-USDT-SWAP", t, triggerNotional: 5_000_000m);
        result!.StrengthScore.Should().Be(100);
    }
}

public class CvdDivergenceDetectorTests
{
    [Fact]
    public void Detect_NullHistory_Throws()
    {
        Action act = () => CvdDivergenceDetector.Detect(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Detect_TooFewBars_ReturnsNull()
    {
        var bars = new List<CvdDivergenceDetector.FlowBar>
        {
            new(DateTime.UtcNow, 100m, 1000m, 800m)
        };
        var result = CvdDivergenceDetector.Detect(bars, windowBars: 60);
        result.Should().BeNull();
    }

    [Fact]
    public void Detect_DistributionPattern_ReturnsSell()
    {
        // Build 60 bars: price climbs from 100 to 110, but CVD declines (whales selling).
        var bars = new List<CvdDivergenceDetector.FlowBar>();
        var t0 = DateTime.UtcNow.AddMinutes(-60);
        for (int i = 0; i < 60; i++)
        {
            // Price strictly rising
            decimal price = 100m + (decimal)i * 10m / 59m;
            // CVD: aggressive sells dominate at ratio 1.2×, so CVD declines.
            decimal buyVol = 100m;
            decimal sellVol = 120m;
            bars.Add(new CvdDivergenceDetector.FlowBar(t0.AddMinutes(i), price, buyVol, sellVol));
        }
        var result = CvdDivergenceDetector.Detect(bars, windowBars: 60);

        result.Should().NotBeNull();
        result!.Type.Should().Be(CvdDivergenceDetector.DivergenceType.Distribution);
        result.Side.Should().Be(TradeSide.Sell);
        result.Correlation.Should().BeLessThan(0, "price up + CVD down → strong negative correlation");
        result.PricePosition.Should().BeApproximately(1.0, 0.05, "current price is at the window high");
    }

    [Fact]
    public void Detect_AccumulationPattern_ReturnsBuy()
    {
        // Price falls from 110 to 100, but CVD rises (whales buying).
        var bars = new List<CvdDivergenceDetector.FlowBar>();
        var t0 = DateTime.UtcNow.AddMinutes(-60);
        for (int i = 0; i < 60; i++)
        {
            decimal price = 110m - (decimal)i * 10m / 59m;
            decimal buyVol = 120m;
            decimal sellVol = 100m;
            bars.Add(new CvdDivergenceDetector.FlowBar(t0.AddMinutes(i), price, buyVol, sellVol));
        }
        var result = CvdDivergenceDetector.Detect(bars, windowBars: 60);

        result.Should().NotBeNull();
        result!.Type.Should().Be(CvdDivergenceDetector.DivergenceType.Accumulation);
        result.Side.Should().Be(TradeSide.Buy);
        result.PricePosition.Should().BeApproximately(0.0, 0.05);
    }

    [Fact]
    public void Detect_NoDivergence_ReturnsNull()
    {
        // Price up + CVD up (normal trend, both positive correlation).
        var bars = new List<CvdDivergenceDetector.FlowBar>();
        var t0 = DateTime.UtcNow.AddMinutes(-60);
        for (int i = 0; i < 60; i++)
        {
            decimal price = 100m + (decimal)i * 10m / 59m;
            decimal buyVol = 200m;  // dominant buys → CVD rises with price
            decimal sellVol = 100m;
            bars.Add(new CvdDivergenceDetector.FlowBar(t0.AddMinutes(i), price, buyVol, sellVol));
        }
        var result = CvdDivergenceDetector.Detect(bars, windowBars: 60);
        result.Should().BeNull();
    }
}

public class MakerExecutionPolicyTests
{
    [Fact]
    public void Plan_BuyPatient_LimitBelowMid()
    {
        var p = MakerExecutionPolicy.Plan(
            side: TradeSide.Buy, mid: 50_000m, topOfBookSpread: 10m,
            urgency: 0.0);
        p.OrderType.Should().Be(OrderType.Limit);
        p.LimitPrice.Should().BeLessThan(50_000m);
        p.TimeoutMs.Should().Be(30_000);
    }

    [Fact]
    public void Plan_SellPatient_LimitAboveMid()
    {
        var p = MakerExecutionPolicy.Plan(
            side: TradeSide.Sell, mid: 50_000m, topOfBookSpread: 10m,
            urgency: 0.0);
        p.LimitPrice.Should().BeGreaterThan(50_000m);
    }

    [Fact]
    public void Plan_Urgent_LimitCloserToMid_AndShortTimeout()
    {
        var patient = MakerExecutionPolicy.Plan(
            TradeSide.Buy, 50_000m, 10m, urgency: 0.0);
        var urgent = MakerExecutionPolicy.Plan(
            TradeSide.Buy, 50_000m, 10m, urgency: 1.0);

        // Urgent buy limit should be HIGHER (closer to mid) than patient one.
        urgent.LimitPrice.Should().BeGreaterThan(patient.LimitPrice);
        urgent.TimeoutMs.Should().BeLessThan(patient.TimeoutMs);
    }

    [Fact]
    public void Plan_ZeroSpread_FallsBackToMarket()
    {
        var p = MakerExecutionPolicy.Plan(
            TradeSide.Buy, 50_000m, topOfBookSpread: 0m, urgency: 0.5);
        p.OrderType.Should().Be(OrderType.Market);
    }

    [Fact]
    public void Plan_InvalidUrgency_Throws()
    {
        Action neg = () => MakerExecutionPolicy.Plan(TradeSide.Buy, 50_000m, 10m, urgency: -0.1);
        neg.Should().Throw<ArgumentOutOfRangeException>();
        Action big = () => MakerExecutionPolicy.Plan(TradeSide.Buy, 50_000m, 10m, urgency: 1.5);
        big.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EstimateRoundTripSavings_FullMaker_NetSavingsPositive()
    {
        // 100% maker fill: each leg pays -2bps (rebate) instead of +5bps (taker).
        // Round trip: -4bps vs +10bps → savings of 14bps.
        double savings = MakerExecutionPolicy.EstimateRoundTripSavingsBps(
            makerFillProbability: 1.0,
            makerRebateBps: -2.0,
            takerFeeBps: 5.0);
        savings.Should().BeApproximately(-14.0, 1e-9);
    }

    [Fact]
    public void EstimateRoundTripSavings_NeverMaker_ZeroSavings()
    {
        double savings = MakerExecutionPolicy.EstimateRoundTripSavingsBps(
            makerFillProbability: 0.0);
        savings.Should().Be(0.0);
    }

    [Fact]
    public void EstimateRoundTripSavings_InvalidProbability_Throws()
    {
        Action act = () => MakerExecutionPolicy.EstimateRoundTripSavingsBps(makerFillProbability: 1.5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
