using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

/// <summary>
/// Unit tests for <see cref="EdgeLabAltShortCapEvaluator.Evaluate"/>. Pure decision
/// logic for the alt-short aggregate exposure cap (2026-05-21 handoff §3).
/// </summary>
public class EdgeLabAltShortCapEvaluatorTests
{
    private const decimal Basis = 2000m;   // cap 0.18 → $360

    [Fact]
    public void NoCap_ReturnsNoCapConfigured()
    {
        var d = EdgeLabAltShortCapEvaluator.Evaluate(
            capFraction: null, basis: Basis,
            targetShortNotionalUsd: 1000m,
            thisStrategyCurrentShortNotionalUsd: 0m,
            groupTotalShortNotionalUsd: 0m,
            isLive: true);

        d.Action.Should().Be(EdgeLabAltShortCapAction.NoCapConfigured);
        d.ShouldBlockSubmit.Should().BeFalse();
    }

    [Fact]
    public void InvalidCapFraction_ReturnsInvalid()
    {
        var d = EdgeLabAltShortCapEvaluator.Evaluate(
            capFraction: 1.5m, basis: Basis,
            targetShortNotionalUsd: 100m,
            thisStrategyCurrentShortNotionalUsd: 0m,
            groupTotalShortNotionalUsd: 0m,
            isLive: true);

        d.Action.Should().Be(EdgeLabAltShortCapAction.InvalidCap);
        d.ShouldBlockSubmit.Should().BeFalse();
    }

    [Fact]
    public void FirstAltShortUnderCap_Allowed()
    {
        // cap = 0.18 × 2000 = $360. op opening $330 short, nothing else → allowed.
        var d = EdgeLabAltShortCapEvaluator.Evaluate(
            capFraction: 0.18m, basis: Basis,
            targetShortNotionalUsd: 330m,
            thisStrategyCurrentShortNotionalUsd: 0m,
            groupTotalShortNotionalUsd: 0m,
            isLive: true);

        d.Action.Should().Be(EdgeLabAltShortCapAction.AllowedUnderCap);
        d.CapUsd.Should().Be(360m);
        d.WouldBeGroupAbsUsd.Should().Be(330m);
        d.ShouldBlockSubmit.Should().BeFalse();
    }

    [Fact]
    public void SecondAltShortWouldBreach_LiveSkips()
    {
        // op already short $330 (this group). fil (this strategy, currently flat) wants
        // to open $120 short. would-be = 330 + 120 = $450 > $360 cap → SKIP in live.
        var d = EdgeLabAltShortCapEvaluator.Evaluate(
            capFraction: 0.18m, basis: Basis,
            targetShortNotionalUsd: 120m,
            thisStrategyCurrentShortNotionalUsd: 0m,
            groupTotalShortNotionalUsd: 330m,
            isLive: true);

        d.Action.Should().Be(EdgeLabAltShortCapAction.SkipBreach);
        d.ShouldBlockSubmit.Should().BeTrue();
        d.WouldBeGroupAbsUsd.Should().Be(450m);
        d.HeadroomUsd.Should().Be(30m); // 360 - 330 other
    }

    [Fact]
    public void SecondAltShortWouldBreach_DryRunWarnsButAllows()
    {
        var d = EdgeLabAltShortCapEvaluator.Evaluate(
            capFraction: 0.18m, basis: Basis,
            targetShortNotionalUsd: 120m,
            thisStrategyCurrentShortNotionalUsd: 0m,
            groupTotalShortNotionalUsd: 330m,
            isLive: false);

        d.Action.Should().Be(EdgeLabAltShortCapAction.WouldBreachInDryRun);
        d.ShouldBlockSubmit.Should().BeFalse();
    }

    [Fact]
    public void ReducingExistingShort_NeverBlocked_EvenWhenGroupOverCap()
    {
        // Group already over cap ($500 > $360), but this strategy is REDUCING its own
        // short from $300 → $150. targetAbs (150) <= thisAbs (300) → always allowed.
        var d = EdgeLabAltShortCapEvaluator.Evaluate(
            capFraction: 0.18m, basis: Basis,
            targetShortNotionalUsd: 150m,
            thisStrategyCurrentShortNotionalUsd: 300m,
            groupTotalShortNotionalUsd: 500m,
            isLive: true);

        d.Action.Should().Be(EdgeLabAltShortCapAction.AllowedUnderCap);
        d.ShouldBlockSubmit.Should().BeFalse();
    }

    [Fact]
    public void RebalancingOwnPiece_PreservesPeers_AllowedWhenWithinCap()
    {
        // Group total $300 of which this strategy holds $200; peers = $100.
        // This strategy re-pegs its piece to $220. would-be = peers(100) + 220 = $320 ≤ $360.
        var d = EdgeLabAltShortCapEvaluator.Evaluate(
            capFraction: 0.18m, basis: Basis,
            targetShortNotionalUsd: 220m,
            thisStrategyCurrentShortNotionalUsd: 200m,
            groupTotalShortNotionalUsd: 300m,
            isLive: true);

        d.Action.Should().Be(EdgeLabAltShortCapAction.AllowedUnderCap);
        d.WouldBeGroupAbsUsd.Should().Be(320m);
        d.ShouldBlockSubmit.Should().BeFalse();
    }

    [Fact]
    public void ExactlyAtCap_Allowed()
    {
        // would-be == cap → allowed (<=, not <).
        var d = EdgeLabAltShortCapEvaluator.Evaluate(
            capFraction: 0.18m, basis: Basis,
            targetShortNotionalUsd: 360m,
            thisStrategyCurrentShortNotionalUsd: 0m,
            groupTotalShortNotionalUsd: 0m,
            isLive: true);

        d.Action.Should().Be(EdgeLabAltShortCapAction.AllowedUnderCap);
        d.ShouldBlockSubmit.Should().BeFalse();
    }
}
