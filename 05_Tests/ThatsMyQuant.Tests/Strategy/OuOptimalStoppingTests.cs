using System;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class OuOptimalStoppingTests
{
    [Fact]
    public void ExpectedReversionPerBar_KnownInput_MatchesAnalytic()
    {
        // h = 10 bars, z = 2.0 → progress = 2.0 · (1 - 2^(-0.1)) ≈ 2.0 · 0.0670 ≈ 0.1340
        double progress = OuOptimalStopping.ExpectedReversionPerBar(absZScore: 2.0, halfLifeBars: 10.0);
        progress.Should().BeApproximately(2.0 * (1.0 - Math.Pow(2.0, -0.1)), 1e-12);
    }

    [Fact]
    public void ExpectedReversionPerBar_InvalidHalfLife_ReturnsZero()
    {
        OuOptimalStopping.ExpectedReversionPerBar(2.0, halfLifeBars: 0).Should().Be(0.0);
        OuOptimalStopping.ExpectedReversionPerBar(2.0, halfLifeBars: -5).Should().Be(0.0);
        OuOptimalStopping.ExpectedReversionPerBar(2.0, halfLifeBars: double.NaN).Should().Be(0.0);
    }

    [Fact]
    public void ExpectedBarsToTarget_AlreadyInTarget_ReturnsZero()
    {
        OuOptimalStopping.ExpectedBarsToTarget(currentZScore: 0.2, targetAbsZ: 0.3, halfLifeBars: 10.0)
            .Should().Be(0.0);
    }

    [Fact]
    public void ExpectedBarsToTarget_FastVsSlowHalfLife_FastReachesSooner()
    {
        // From z=2.0 to z*=0.3 with h=5 vs h=20. Faster half-life reaches target sooner.
        double fast = OuOptimalStopping.ExpectedBarsToTarget(2.0, 0.3, halfLifeBars: 5.0);
        double slow = OuOptimalStopping.ExpectedBarsToTarget(2.0, 0.3, halfLifeBars: 20.0);
        fast.Should().BeLessThan(slow);
        // h=5: 5 · log2(2.0/0.3) = 5 · 2.737 = 13.69
        fast.Should().BeApproximately(5.0 * Math.Log(2.0 / 0.3) / Math.Log(2.0), 1e-9);
    }

    [Fact]
    public void ExpectedBarsToTarget_InvalidHalfLife_ReturnsInfinity()
    {
        var result = OuOptimalStopping.ExpectedBarsToTarget(2.0, 0.3, halfLifeBars: 0);
        double.IsPositiveInfinity(result).Should().BeTrue();
    }

    [Fact]
    public void ShouldExitForNegativeRemainingEdge_TightZWithinCost_ReturnsTrue()
    {
        // [F2] sigma-normalized. σ_spread = 0.005 (small-cap spread), cost = 0.002 →
        // costInSigmas = 0.4. z = 0.4, target = 0.3 → remaining = 0.1σ < 0.4 → exit.
        bool exit = OuOptimalStopping.ShouldExitForNegativeRemainingEdge(
            currentZScore: 0.4,
            targetAbsZ: 0.3,
            spreadStdDev: 0.005,
            referencePrice: 50_000,
            roundTripCostFraction: 0.0020);
        exit.Should().BeTrue();
    }

    [Fact]
    public void ShouldExitForNegativeRemainingEdge_LargeZAboveCost_ReturnsFalse()
    {
        // [F2] σ = 0.005, cost = 0.002 → costInSigmas = 0.4. z=2.5, tgt=0.3 → rem=2.2σ ≫ 0.4 → hold.
        bool exit = OuOptimalStopping.ShouldExitForNegativeRemainingEdge(
            currentZScore: 2.5,
            targetAbsZ: 0.3,
            spreadStdDev: 0.005,
            referencePrice: 50_000,
            roundTripCostFraction: 0.0020);
        exit.Should().BeFalse();
    }

    [Fact]
    public void ShouldExitForNegativeRemainingEdge_AlreadyInsideTarget_ReturnsFalse()
    {
        // |z| ≤ target — caller's TP path handles this; we don't fire.
        bool exit = OuOptimalStopping.ShouldExitForNegativeRemainingEdge(
            currentZScore: 0.2,
            targetAbsZ: 0.3,
            spreadStdDev: 0.005,
            referencePrice: 50_000,
            roundTripCostFraction: 0.0020);
        exit.Should().BeFalse();
    }

    [Fact]
    public void ShouldExitForNegativeRemainingEdge_LargeSpreadHoldsAcrossLegPriceScales()
    {
        // [F2] When σ_spread is large (mature high-cap pair), costInSigmas → 0 and the
        // gate effectively never trips on remaining-edge alone. Previously the leg-A
        // refPrice bridge made σ_spread/refPrice tiny on these pairs and forced premature
        // exit; sigma-normalization eliminates that scale dependence.
        bool exit = OuOptimalStopping.ShouldExitForNegativeRemainingEdge(
            currentZScore: 0.31,
            targetAbsZ: 0.3,
            spreadStdDev: 100,        // BCH/LTC-style σ in price units
            referencePrice: 50_000,
            roundTripCostFraction: 0.0020);
        exit.Should().BeFalse();
    }

    [Fact]
    public void ShouldExitForTimeBudget_HeldShort_ReturnsFalse()
    {
        // h=20, multiplier=2 → budget=40 bars. Held=15 → hold.
        OuOptimalStopping.ShouldExitForTimeBudget(barsHeld: 15, halfLifeBars: 20.0, multiplier: 2.0)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldExitForTimeBudget_HeldPastBudget_ReturnsTrue()
    {
        // h=20, multiplier=2 → budget=40 bars. Held=42 → exit.
        OuOptimalStopping.ShouldExitForTimeBudget(barsHeld: 42, halfLifeBars: 20.0, multiplier: 2.0)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldExitForTimeBudget_InvalidHalfLife_ReturnsFalse()
    {
        OuOptimalStopping.ShouldExitForTimeBudget(barsHeld: 999, halfLifeBars: 0).Should().BeFalse();
        OuOptimalStopping.ShouldExitForTimeBudget(barsHeld: 999, halfLifeBars: double.NaN).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_NegativeRemainingEdge_FiresFirst()
    {
        var d = OuOptimalStopping.Evaluate(
            barsHeld: 5,
            currentZScore: 0.4,
            targetAbsZ: 0.3,
            halfLifeBars: 20,
            spreadStdDev: 0.005,
            referencePrice: 50_000,
            roundTripCostFraction: 0.0020);
        d.ShouldExit.Should().BeTrue();
        d.Reason.Should().Be("OU:NegativeRemainingEdge");
    }

    [Fact]
    public void Evaluate_TimeBudgetExceeded_FiresWhenAppropriate()
    {
        // High z (cost not the issue), but held too long.
        var d = OuOptimalStopping.Evaluate(
            barsHeld: 60,
            currentZScore: 2.5,
            targetAbsZ: 0.3,
            halfLifeBars: 20,
            spreadStdDev: 100,
            referencePrice: 50_000,
            roundTripCostFraction: 0.0020,
            timeBudgetMultiplier: 2.0);
        d.ShouldExit.Should().BeTrue();
        d.Reason.Should().Be("OU:TimeBudgetExceeded");
    }

    [Fact]
    public void Evaluate_BothConditionsHealthy_HoldsPosition()
    {
        var d = OuOptimalStopping.Evaluate(
            barsHeld: 10,
            currentZScore: 2.0,
            targetAbsZ: 0.3,
            halfLifeBars: 20,
            spreadStdDev: 100,
            referencePrice: 50_000,
            roundTripCostFraction: 0.0020);
        d.ShouldExit.Should().BeFalse();
        d.Reason.Should().Be("OU:Hold");
    }
}
