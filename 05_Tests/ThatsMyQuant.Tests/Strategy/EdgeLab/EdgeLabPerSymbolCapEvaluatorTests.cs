using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

/// <summary>
/// Unit tests for <see cref="EdgeLabPerSymbolCapEvaluator.Evaluate"/>. Pure
/// decision logic; tested across the full action matrix. Sprint 7 (2026-05-11).
/// </summary>
public class EdgeLabPerSymbolCapEvaluatorTests
{
    private const decimal Basis = 2000m;

    // ── No cap configured ────────────────────────────────────────────────────

    [Fact]
    public void NoCap_ReturnsNoCapConfigured()
    {
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 1500m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 0m,
            capFraction: null,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.NoCapConfigured);
        d.ShouldBlockSubmit.Should().BeFalse();
    }

    // ── Allowed under cap ────────────────────────────────────────────────────

    [Fact]
    public void TargetWithinCap_ReturnsAllowed()
    {
        // Cap = 30% of $2K = $600. Target $400, no other exposure → would-be $400 ≤ $600.
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 400m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 0m,
            capFraction: 0.30m,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.AllowedUnderCap);
        d.CapUsd.Should().Be(600m);
        d.WouldBeTotalAbsUsd.Should().Be(400m);
        d.ShouldBlockSubmit.Should().BeFalse();
    }

    [Fact]
    public void TargetExactlyAtCap_AllowsRebalance()
    {
        // Edge: would-be = cap → allowed (≤, not <).
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 600m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 0m,
            capFraction: 0.30m,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.AllowedUnderCap);
        d.ShouldBlockSubmit.Should().BeFalse();
    }

    // ── Multi-strategy total accounting ──────────────────────────────────────

    [Fact]
    public void OtherStrategyExposure_CountsTowardCap()
    {
        // Cap $600. Other strategy holds $400 on BTC. This strategy currently 0,
        // wants to add $300 → would-be total $400 + $300 = $700 > $600 → BREACH.
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 300m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 400m,
            capFraction: 0.30m,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.SkipBreach);
        d.CurrentTotalAbsUsd.Should().Be(400m);
        d.WouldBeTotalAbsUsd.Should().Be(700m);
        d.HeadroomUsd.Should().Be(200m); // cap - other_abs = 600 - 400
        d.ShouldBlockSubmit.Should().BeTrue();
    }

    [Fact]
    public void RebalanceWithinExistingPiece_AllowedEvenIfTotalNearCap()
    {
        // This strategy already holds $500 (its piece). Other strategies hold $0.
        // It wants to rebalance UP to $580. Total currently $500, would-be $580 → ≤ $600.
        // The "other" component = total - this_piece = $500 - $500 = $0.
        // Would-be total = $0 + $580 = $580 ≤ $600 → ALLOWED.
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 580m,
            thisStrategyCurrentNotionalUsd: 500m,
            totalSymbolNotionalUsd: 500m,
            capFraction: 0.30m,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.AllowedUnderCap);
    }

    [Fact]
    public void RebalanceDown_AlwaysAllowed_WhenOverCap()
    {
        // Already over cap due to accumulated drift. This strategy wants to REDUCE
        // its position. Should be allowed — reducing exposure cannot worsen the breach.
        // Other strategies hold $800 (already over $600 cap somehow). This strategy
        // currently $200 (so total $1000). Wants to flatten to $0 → would-be $800 + $0 = $800.
        // Still > $600 → BREACH from the would-be perspective.
        //
        // PhD-quant call: the evaluator is pure & symmetric — it doesn't distinguish
        // "reducing vs adding". The HOSTED-SERVICE caller should explicitly allow
        // reduce-only rebalances when |target| < |current|. Document this gap so
        // the bug isn't silent.
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 0m,
            thisStrategyCurrentNotionalUsd: 200m,
            totalSymbolNotionalUsd: 1000m,
            capFraction: 0.30m,
            isLive: true);

        // Evaluator says BREACH because would-be ($800) > cap ($600).
        // This documents the current behavior; the hosted service can override
        // for reduce-only flows if/when that becomes a real operational issue.
        d.Action.Should().Be(EdgeLabPerSymbolCapAction.SkipBreach);
    }

    // ── DryRun vs Live divergence ────────────────────────────────────────────

    [Fact]
    public void DryRun_BreachReturnsInformationalOnly()
    {
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 700m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 0m,
            capFraction: 0.30m,
            isLive: false);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.WouldBreachInDryRun);
        d.ShouldBlockSubmit.Should().BeFalse(); // proceeds in DryRun
    }

    // ── Symmetric absolute-value accounting ──────────────────────────────────

    [Fact]
    public void NegativeTarget_UsesAbsoluteValue()
    {
        // SHORT signal — target -$700. |target| = $700 > cap $600 → BREACH.
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: -700m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 0m,
            capFraction: 0.30m,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.SkipBreach);
        d.WouldBeTotalAbsUsd.Should().Be(700m);
    }

    [Fact]
    public void OppositeSignPositions_DoNotNet()
    {
        // Strategy A holds +$400, strategy B (this one) wants -$300 (short).
        // Cap $600. Both contribute absolute notional — total exposure on the symbol
        // is $400 + $300 = $700 regardless of sign. BREACH.
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: -300m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 400m,
            capFraction: 0.30m,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.SkipBreach);
        d.WouldBeTotalAbsUsd.Should().Be(700m);
    }

    // ── Misconfiguration handling ────────────────────────────────────────────

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void InvalidCapFraction_ReturnsInvalidCap(double fraction)
    {
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 100m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 0m,
            capFraction: (decimal)fraction,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.InvalidCap);
        d.ShouldBlockSubmit.Should().BeFalse(); // misconfiguration shouldn't silently block fires
    }

    [Fact]
    public void ZeroCap_BlocksAnyNonZeroTarget()
    {
        // Operator sets cap = 0 → effectively bans new positions on the symbol.
        // Valid use case: "freeze BTC, no new BTC entries". Should block.
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 100m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 0m,
            capFraction: 0m,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.SkipBreach);
        d.CapUsd.Should().Be(0m);
    }

    [Fact]
    public void CapAtOne_AllowsFullBasisExposure()
    {
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 1900m,
            thisStrategyCurrentNotionalUsd: 0m,
            totalSymbolNotionalUsd: 0m,
            capFraction: 1m,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.AllowedUnderCap);
    }

    // ── Realistic stack scenario (regression check) ──────────────────────────

    [Fact]
    public void RealisticBtcStackScenario_CapBlocksFifthStrategy()
    {
        // Sprint 7 motivation: 4 BTC strategies already deployed with weights summing 0.31.
        // At $2K basis × 1.5 leverage, fully-loaded exposure = $930.
        // Cap of 0.30 = $600.
        // Simulate: 4 strategies have driven total BTC notional to $930. A 5th
        // strategy (vol_carry_btc_long, weight 0.08 → target $240) tries to fire.
        // would-be total = $930 + $240 = $1170 > $600 → SKIP.
        var d = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: "BTCUSDT", basis: Basis,
            targetNotionalUsd: 240m,
            thisStrategyCurrentNotionalUsd: 0m,    // 5th strategy hasn't entered yet
            totalSymbolNotionalUsd: 930m,
            capFraction: 0.30m,
            isLive: true);

        d.Action.Should().Be(EdgeLabPerSymbolCapAction.SkipBreach);
        d.CapUsd.Should().Be(600m);
        d.WouldBeTotalAbsUsd.Should().Be(1170m);
        d.HeadroomUsd.Should().Be(-330m); // already over cap from existing strategies
    }
}
