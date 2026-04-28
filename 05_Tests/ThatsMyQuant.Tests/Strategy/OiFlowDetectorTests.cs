using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class OiFlowDetectorTests
{
    [Fact]
    public void Classify_OiUpPriceUp_ReturnsTrendContinuation()
    {
        var r = OiFlowDetector.Classify(oiPrev: 1000m, oiNow: 1020m, pricePrev: 100m, priceNow: 101m);
        r.Regime.Should().Be(OiFlowDetector.FlowRegime.TrendContinuation);
        r.SizeMultiplierForMeanReversion.Should().Be(0.5);
    }

    [Fact]
    public void Classify_OiUpPriceDown_ReturnsFreshShorts()
    {
        var r = OiFlowDetector.Classify(oiPrev: 1000m, oiNow: 1020m, pricePrev: 101m, priceNow: 100m);
        r.Regime.Should().Be(OiFlowDetector.FlowRegime.FreshShorts);
        r.SizeMultiplierForMeanReversion.Should().Be(1.1);
    }

    [Fact]
    public void Classify_OiDown_ReturnsPositionUnwind_RegardlessOfPriceDirection()
    {
        var rUp = OiFlowDetector.Classify(oiPrev: 1000m, oiNow: 980m, pricePrev: 100m, priceNow: 101m);
        var rDown = OiFlowDetector.Classify(oiPrev: 1000m, oiNow: 980m, pricePrev: 101m, priceNow: 100m);
        rUp.Regime.Should().Be(OiFlowDetector.FlowRegime.PositionUnwind);
        rDown.Regime.Should().Be(OiFlowDetector.FlowRegime.PositionUnwind);
        rUp.SizeMultiplierForMeanReversion.Should().Be(1.2);
    }

    [Fact]
    public void Classify_BelowThresholds_ReturnsNeutral()
    {
        // 0.05% OI change — below 0.5% default threshold.
        var r = OiFlowDetector.Classify(oiPrev: 1000m, oiNow: 1000.5m, pricePrev: 100m, priceNow: 100.05m);
        r.Regime.Should().Be(OiFlowDetector.FlowRegime.Neutral);
        r.SizeMultiplierForMeanReversion.Should().Be(1.0);
    }

    [Fact]
    public void Classify_NonPositiveInputs_ReturnsNeutral()
    {
        var r = OiFlowDetector.Classify(oiPrev: 0m, oiNow: 1020m, pricePrev: 100m, priceNow: 101m);
        r.Regime.Should().Be(OiFlowDetector.FlowRegime.Neutral);
    }

    [Fact]
    public void DetectCascadeRisk_LargeOiSwingPlusFundingSpike_Trips()
    {
        // 16% OI move + 5 bp funding spike
        OiFlowDetector.DetectCascadeRisk(oiPrev: 1000m, oiNow: 1160m, fundingRateBps: 5.0).Should().BeTrue();
        // Symmetric direction (negative funding): 16% drop in OI
        OiFlowDetector.DetectCascadeRisk(oiPrev: 1000m, oiNow: 840m, fundingRateBps: -5.0).Should().BeTrue();
    }

    [Fact]
    public void DetectCascadeRisk_OnlyOneConditionMet_DoesNotTrip()
    {
        // Big OI move, calm funding
        OiFlowDetector.DetectCascadeRisk(oiPrev: 1000m, oiNow: 1200m, fundingRateBps: 1.0).Should().BeFalse();
        // Small OI move, big funding
        OiFlowDetector.DetectCascadeRisk(oiPrev: 1000m, oiNow: 1050m, fundingRateBps: 10.0).Should().BeFalse();
    }
}

public class OiFlowStateServiceTests
{
    [Fact]
    public void ClassifyAt_AggregatesAcrossWindow()
    {
        var svc = new OiFlowStateService();
        var t0 = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);

        svc.RecordOi("BTC-USDT", 1000m, t0, price: 60_000m);
        svc.RecordOi("BTC-USDT", 1010m, t0.AddMinutes(30), price: 60_300m);
        svc.RecordOi("BTC-USDT", 1030m, t0.AddMinutes(60), price: 60_600m);

        var r = svc.ClassifyAt("BTC-USDT", System.TimeSpan.FromMinutes(60));
        r.Regime.Should().Be(OiFlowDetector.FlowRegime.TrendContinuation);
    }

    [Fact]
    public void ClassifyAt_NoDataYet_ReturnsNeutral()
    {
        var svc = new OiFlowStateService();
        var r = svc.ClassifyAt("BTC-USDT", System.TimeSpan.FromMinutes(60));
        r.Regime.Should().Be(OiFlowDetector.FlowRegime.Neutral);
    }

    [Fact]
    public void DetectCascadeRiskAt_MeasuresFromWindowEdge()
    {
        var svc = new OiFlowStateService();
        var t0 = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);

        svc.RecordOi("BTC-USDT", 1000m, t0, price: 60_000m);
        svc.RecordOi("BTC-USDT", 1180m, t0.AddHours(4), price: 62_000m);

        svc.DetectCascadeRiskAt("BTC-USDT", System.TimeSpan.FromHours(4), fundingRateBps: 5.0)
            .Should().BeTrue();
    }
}
