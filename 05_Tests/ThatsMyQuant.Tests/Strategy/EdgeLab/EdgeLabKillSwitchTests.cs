using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

/// <summary>
/// Unit tests for <see cref="EdgeLabKillSwitch.Resolve"/>. Sprint 6 (2026-05-10).
/// Validates the contract decision matrix:
///   realized30c >= soft → 1.0×
///   hard <= realized30c < soft → 0.5×
///   realized30c < hard → 0.0×
///   n &lt; min → 1.0× (insufficient sample)
/// </summary>
public class EdgeLabKillSwitchTests
{
    private static IReadOnlyList<decimal> Returns(params decimal[] xs) => xs;

    private static IReadOnlyList<decimal> ConstantReturns(decimal mean, decimal std, int n, int seed = 0)
    {
        // Deterministic sample with EXACT target mean and std (sample-std with n-1).
        // Pattern alternates [mean+std, mean-std]; requires even n so the mean lands precisely.
        // Sample variance = Σ(r-mean)² / (n-1) = n·std² / (n-1) → returned std slightly
        // larger than `std` (acceptable for tests that bracket the result, not assert equality).
        if (n % 2 != 0) n++;
        var list = new List<decimal>(n);
        for (int i = 0; i < n; i++)
        {
            list.Add(mean + (i % 2 == 0 ? std : -std));
        }
        return list;
    }

    [Fact]
    public void Resolve_InsufficientSample_FullKelly()
    {
        var d = EdgeLabKillSwitch.Resolve(Returns(0.01m, 0.02m), 30, -0.3, -1.0);
        d.Multiplier.Should().Be(EdgeLabKillSwitch.FullKellyMultiplier);
        d.Reason.Should().Contain("insufficient");
    }

    [Fact]
    public void Resolve_StrongPositive_FullKelly()
    {
        // mean +2%, std 1%, 12 cycles, 30 cyc/yr → Sharpe ann = (0.02/0.01) * sqrt(30) ≈ 11
        var d = EdgeLabKillSwitch.Resolve(ConstantReturns(0.02m, 0.01m, 12), 30, -0.3, -1.0);
        d.Multiplier.Should().Be(EdgeLabKillSwitch.FullKellyMultiplier);
        d.RealizedSharpeAnn.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Resolve_BetweenSoftAndHard_HalfKelly()
    {
        // Target Sharpe ann between -0.3 and -1.0 with 30 cyc/yr (sqrt=5.477).
        // mean=-0.005, std=0.05 → per-cycle Sharpe ≈ -0.1 → ann ≈ -0.55.
        var d = EdgeLabKillSwitch.Resolve(ConstantReturns(-0.005m, 0.05m, 12), 30, -0.3, -1.0);
        ((double)d.RealizedSharpeAnn).Should().BeInRange(-1.0, -0.3);
        d.Multiplier.Should().Be(EdgeLabKillSwitch.HalfKellyMultiplier);
    }

    [Fact]
    public void Resolve_BelowHard_HardKill()
    {
        // mean=-0.05, std=0.05 → per-cycle Sharpe ≈ -1 → ann ≈ -5.4 (well below -1).
        var d = EdgeLabKillSwitch.Resolve(ConstantReturns(-0.05m, 0.05m, 12), 30, -0.3, -1.0);
        ((double)d.RealizedSharpeAnn).Should().BeLessThan(-1.0);
        d.Multiplier.Should().Be(EdgeLabKillSwitch.HardKillMultiplier);
    }

    [Fact]
    public void Resolve_TruncatesTo30Cycles()
    {
        // 50 cycles of strong positive should still register full Kelly + use only the last 30.
        var d = EdgeLabKillSwitch.Resolve(ConstantReturns(0.02m, 0.01m, 50), 30, -0.3, -1.0);
        d.NCycles.Should().Be(30);
        d.Multiplier.Should().Be(EdgeLabKillSwitch.FullKellyMultiplier);
    }

    [Fact]
    public void Resolve_ZeroStd_DegenerateGivesFullKelly()
    {
        // All identical returns → std=0 → sharpe=0 → above any threshold → full Kelly.
        var d = EdgeLabKillSwitch.Resolve(Enumerable.Repeat(0.01m, 12).ToList(), 30, -0.3, -1.0);
        d.Multiplier.Should().Be(EdgeLabKillSwitch.FullKellyMultiplier);
    }

    [Fact]
    public void State_RoundTripsViaJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kill_switch_test_{Guid.NewGuid():N}.json");
        try
        {
            var s = new EdgeLabKillSwitchState(path);
            var doc = new EdgeLabKillSwitchState.StateDocument(
                DateTime.UtcNow,
                new Dictionary<string, EdgeLabKillSwitchState.StrategyState>(StringComparer.OrdinalIgnoreCase)
                {
                    ["fee_panic_btc_long"] = new(
                        Strategy: "fee_panic_btc_long",
                        Multiplier: 0.5m,
                        RealizedSharpeAnn: -0.6m,
                        NCycles: 18,
                        Reason: "demoted",
                        UpdatedUtc: DateTime.UtcNow)
                });
            s.Write(doc);

            var read = s.Read();
            read.Strategies.Should().ContainKey("fee_panic_btc_long");
            read.Strategies["fee_panic_btc_long"].Multiplier.Should().Be(0.5m);
            // Case-insensitive lookup
            s.GetMultiplier("FEE_PANIC_BTC_LONG").Should().Be(0.5m);
            // Unknown strategy default = full Kelly
            s.GetMultiplier("nonexistent").Should().Be(1.0m);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void State_MissingFile_GetMultiplierIsFullKelly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kill_switch_missing_{Guid.NewGuid():N}.json");
        var s = new EdgeLabKillSwitchState(path);
        s.GetMultiplier("any").Should().Be(EdgeLabKillSwitch.FullKellyMultiplier);
    }
}
