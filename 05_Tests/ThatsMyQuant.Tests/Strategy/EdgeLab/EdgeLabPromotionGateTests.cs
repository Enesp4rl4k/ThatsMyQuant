using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

/// <summary>
/// Unit tests for <see cref="EdgeLabPromotionGate.Resolve"/>. Pure function
/// with no IO — tested directly across the full decision matrix from the
/// xmldoc on the gate. Sprint 2 (2026-05-10).
/// </summary>
public class EdgeLabPromotionGateTests
{
    // ── declared=off → always off ────────────────────────────────────────────

    [Theory]
    [InlineData("verified")]
    [InlineData("soft-promote")]
    [InlineData("observation")]
    [InlineData("unknown-status")]
    public void Resolve_DeclaredOff_AlwaysOff(string status)
    {
        var d = EdgeLabPromotionGate.Resolve("off", status, paperCyclesCompleted: 100, minPaperCyclesForLive: 50);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.Off);
        d.Reason.Should().Contain("declared=off");
    }

    // ── declared=dry-run → stays dry-run regardless of status ────────────────

    [Theory]
    [InlineData("verified")]
    [InlineData("soft-promote")]
    [InlineData("observation")]
    public void Resolve_DeclaredDryRun_StaysDryRun(string status)
    {
        var d = EdgeLabPromotionGate.Resolve("dry-run", status, 100, 50);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.DryRun);
        d.Reason.Should().Contain("dry-run");
    }

    // ── declared=live + observation → demoted to dry-run ─────────────────────

    [Fact]
    public void Resolve_LiveWithObservationStatus_ForcesDryRun()
    {
        var d = EdgeLabPromotionGate.Resolve("live", "observation", 1000, 0);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.DryRun);
        d.Reason.Should().Contain("observation");
    }

    // ── declared=live + verified → live unconditionally ──────────────────────

    [Fact]
    public void Resolve_LiveWithVerifiedStatus_AlwaysLive()
    {
        var d = EdgeLabPromotionGate.Resolve("live", "verified", paperCyclesCompleted: 0, minPaperCyclesForLive: 0);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.Live);
    }

    [Fact]
    public void Resolve_LiveWithVerifiedStatus_IgnoresPaperCyclesGate()
    {
        // Verified strategies bypass the paper-cycles gate even if min is set.
        // Rationale: verified means full paranoid pass; no observation needed.
        var d = EdgeLabPromotionGate.Resolve("live", "verified", paperCyclesCompleted: 0, minPaperCyclesForLive: 999);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.Live);
    }

    // ── declared=live + soft-promote: gated on paper cycles ──────────────────

    [Fact]
    public void Resolve_LiveSoftPromote_GateDisabled_AllowsLive()
    {
        // MinPaperCyclesForLive=0 disables the gate even for soft-promote — used
        // for already-deployed strategies pre-dating Sprint 2 (back-compat).
        var d = EdgeLabPromotionGate.Resolve("live", "soft-promote", paperCyclesCompleted: 0, minPaperCyclesForLive: 0);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.Live);
        d.Reason.Should().Contain("gate=disabled");
    }

    [Fact]
    public void Resolve_LiveSoftPromote_BelowGate_ForcesDryRun()
    {
        // Soft-promote with cycles below threshold demotes to dry-run.
        // This is the primary new-edge guard.
        var d = EdgeLabPromotionGate.Resolve("live", "soft-promote", paperCyclesCompleted: 5, minPaperCyclesForLive: 10);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.DryRun);
        d.Reason.Should().Contain("paper_cycles=5");
        d.Reason.Should().Contain("min=10");
        d.Reason.Should().Contain("pending");
    }

    [Fact]
    public void Resolve_LiveSoftPromote_AtGateBoundary_PromotesToLive()
    {
        // cycles == min: gate passes. Strict >= boundary; one fewer would fail.
        var d = EdgeLabPromotionGate.Resolve("live", "soft-promote", paperCyclesCompleted: 10, minPaperCyclesForLive: 10);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.Live);
        d.Reason.Should().Contain("promoted");
    }

    [Fact]
    public void Resolve_LiveSoftPromote_AboveGate_PromotesToLive()
    {
        var d = EdgeLabPromotionGate.Resolve("live", "soft-promote", paperCyclesCompleted: 50, minPaperCyclesForLive: 10);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.Live);
    }

    [Fact]
    public void Resolve_LiveUnknownStatus_TreatedAsVerifiedForBackCompat()
    {
        // Strategies pre-dating PromotionStatus default to "verified" semantics.
        var d = EdgeLabPromotionGate.Resolve("live", "unknown-future-status", 0, 999);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.Live);
    }

    [Fact]
    public void Resolve_NullPromotionStatus_TreatedAsVerified()
    {
        var d = EdgeLabPromotionGate.Resolve("live", null!, 0, 999);
        d.EffectiveMode.Should().Be(EdgeLabHybridMode.Live);
    }

    // ── NormalizeMode helper ─────────────────────────────────────────────────

    [Theory]
    [InlineData("off", EdgeLabHybridMode.Off)]
    [InlineData("OFF", EdgeLabHybridMode.Off)]
    [InlineData(" Off ", EdgeLabHybridMode.Off)]
    [InlineData("dry-run", EdgeLabHybridMode.DryRun)]
    [InlineData("DRY-RUN", EdgeLabHybridMode.DryRun)]
    [InlineData("live", EdgeLabHybridMode.Live)]
    [InlineData("LIVE", EdgeLabHybridMode.Live)]
    [InlineData("garbage", EdgeLabHybridMode.DryRun)]  // safe default
    [InlineData("", EdgeLabHybridMode.DryRun)]
    [InlineData(null, EdgeLabHybridMode.DryRun)]
    public void NormalizeMode_HandlesAllInputs(string? input, EdgeLabHybridMode expected)
    {
        EdgeLabPromotionGate.NormalizeMode(input).Should().Be(expected);
    }
}
