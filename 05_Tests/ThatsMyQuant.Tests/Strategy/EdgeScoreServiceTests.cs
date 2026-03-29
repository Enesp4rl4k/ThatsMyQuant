using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class EdgeScoreServiceTests
{
    private readonly EdgeScoreService _service = new();

    [Fact]
    public void Evaluate_ShouldReject_WhenExecutionEdgeFallsBelowHardGate()
    {
        var signal = CreateSignal("BTC-USD");
        var decision = CreateDecision("HftMicroEdge", "Volatile", 88);
        var result = _service.Evaluate(
            signal,
            decision,
            new EvaluationGuards(
                new ExpectancyGuardResult(true, 1.0m, "ok", 10, 40m, 1.8),
                new SymbolGuardResult(true, 1.0m, "ok"),
                new PromotionGuardResult(false, 0m, "HFT latency caution with latency", false, 0.30, "Paper"),
                (true, "ok"),
                (true, "ok", 0.01m)));

        result.Approved.Should().BeFalse();
        result.ExecutionEdge.Should().Be(62m); // baseline 50 + 12 (volume >= 2× ADV)
    }

    [Fact]
    public void Evaluate_ShouldReject_WhenRiskContextFallsBelowHardGate()
    {
        var signal = CreateSignal("THYAO", new DateTime(2026, 03, 21, 22, 0, 0, DateTimeKind.Utc));
        var decision = CreateDecision("TrendConfluence", "Volatile", 80);
        var result = _service.Evaluate(
            signal,
            decision,
            new EvaluationGuards(
                new ExpectancyGuardResult(true, 1.0m, "ok", 10, 30m, 1.6),
                new SymbolGuardResult(true, 1.0m, "ok"),
                new PromotionGuardResult(true, 1.0m, "ok", true, 0.10, "ScaledLive"),
                (false, "daily loss"),
                (false, "risk veto", 0m)));

        result.Approved.Should().BeFalse();
        result.RiskContext.Should().BeLessThan(50m);
    }

    [Fact]
    public void Evaluate_ShouldRecommendReducedSize_ForSeventyToEightyFourBand()
    {
        // High-confidence trending signal with strong guards → lands in "reduced" band
        var signal = CreateSignal("THYAO");
        signal.Volume24h = 50_000_000m; // Very high volume → execution edge boost to 62
        var decision = CreateDecision("TrendConfluence", "Trending", 95);
        var result = _service.Evaluate(
            signal,
            decision,
            new EvaluationGuards(
                new ExpectancyGuardResult(true, 1.0m, "ok", 50, 30m, 2.0),
                new SymbolGuardResult(true, 1.0m, "ok"),
                new PromotionGuardResult(true, 1.0m, "ok", true, 0.0, "ScaledLive"),
                (true, "ok"),
                (true, "ok", 0.025m)));

        result.Approved.Should().BeTrue();
        result.DecisionBand.Should().Be("reduced");
        result.RecommendedSizeMultiplier.Should().Be(0.65m);
        result.AdjustedEdgeScore.Should().BeInRange(70m, 84.99m);
    }

    [Fact]
    public void Evaluate_ShouldRecommendProbe_ForFiftyFiveToSixtyNineBand()
    {
        // Lower-confidence ranging signal with moderate guards → lands in "probe" band
        var signal = CreateSignal("THYAO");
        signal.Volume24h = 50_000_000m;
        var decision = CreateDecision("RangeReversion", "Ranging", 65);
        var result = _service.Evaluate(
            signal,
            decision,
            new EvaluationGuards(
                new ExpectancyGuardResult(true, 0.60m, "soft", 10, 4m, 1.12),
                new SymbolGuardResult(true, 0.55m, "soft"),
                new PromotionGuardResult(true, 0.55m, "caution", false, 0.35, "LiveProbe"),
                (true, "ok"),
                (true, "ok", 0.015m)));

        result.Approved.Should().BeFalse();
        result.DecisionBand.Should().Be("probe");
        result.AdjustedEdgeScore.Should().BeInRange(55m, 69.99m);
    }

    private static Signal CreateSignal(string symbol, DateTime? timestamp = null) => new()
    {
        Symbol = symbol,
        Price = 100m,
        Timestamp = timestamp ?? new DateTime(2026, 03, 21, 11, 0, 0, DateTimeKind.Utc),
        Volume24h = 2_500_000m
    };

    private static TradeDecision CreateDecision(string strategyTag, string regimeTag, int confidence) => new()
    {
        ShouldTrade = true,
        Side = TradeSide.Buy,
        Confidence = confidence,
        PositionSizePercent = 0.03m,
        StrategyTag = strategyTag,
        RegimeTag = regimeTag,
        Indicators = new IndicatorSnapshot
        {
            CurrentPrice = 100m,
            VolumeRatio = 1.4
        }
    };
}
