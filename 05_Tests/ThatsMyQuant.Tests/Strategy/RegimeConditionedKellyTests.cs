using System;
using System.Collections.Generic;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class RegimeConditionedKellyTests
{
    [Fact]
    public void Compute_NullHistory_Throws()
    {
        Action act = () => RegimeConditionedKelly.Compute(MarketRegime.Ranging, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Compute_InvalidFractionalKelly_Throws()
    {
        var history = new List<RegimeConditionedKelly.RegimeStats>();
        Action zero = () => RegimeConditionedKelly.Compute(MarketRegime.Ranging, history, fractionalKelly: 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();
        Action big = () => RegimeConditionedKelly.Compute(MarketRegime.Ranging, history, fractionalKelly: 1.5);
        big.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Compute_EmptyHistory_ReturnsZero()
    {
        var result = RegimeConditionedKelly.Compute(MarketRegime.Ranging, Array.Empty<RegimeConditionedKelly.RegimeStats>());
        result.Fraction.Should().Be(0.0);
        result.UsedFallback.Should().BeTrue();
    }

    [Fact]
    public void Compute_KnownInput_MatchesAnalyticKelly()
    {
        // p=0.6, b=2.0 → Kelly = (0.6*2 - 0.4)/2 = 0.8/2 = 0.4 → quarter-Kelly = 0.1
        var stats = new[]
        {
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Ranging, Wins: 60, Losses: 40, AverageWin: 200.0, AverageLoss: 100.0)
        };
        var result = RegimeConditionedKelly.Compute(
            MarketRegime.Ranging, stats, fractionalKelly: 0.25, minSamplesForRegime: 20);

        result.RawFullKelly.Should().BeApproximately(0.4, 1e-9);
        result.Fraction.Should().BeApproximately(0.1, 1e-9);
        result.UsedFallback.Should().BeFalse();
        result.UsedRegime.Should().Be(MarketRegime.Ranging);
    }

    [Fact]
    public void Compute_NegativeEdge_ReturnsZero()
    {
        // p=0.4, b=1.0 → Kelly = (0.4 - 0.6)/1 = -0.2 → no trade.
        var stats = new[]
        {
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Trending, Wins: 40, Losses: 60, AverageWin: 100.0, AverageLoss: 100.0)
        };
        var result = RegimeConditionedKelly.Compute(MarketRegime.Trending, stats);
        result.Fraction.Should().Be(0.0);
        result.RawFullKelly.Should().BeLessThan(0);
    }

    [Fact]
    public void Compute_InsufficientSamplesInRegime_FallsBackToPooled()
    {
        // Current regime has only 10 samples (below default 20 min). Pooled: 60+40 = 100 → use pooled.
        var stats = new[]
        {
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Ranging, Wins: 60, Losses: 40, AverageWin: 200.0, AverageLoss: 100.0),
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Volatile, Wins: 6, Losses: 4, AverageWin: 200.0, AverageLoss: 100.0)
        };
        var result = RegimeConditionedKelly.Compute(MarketRegime.Volatile, stats, minSamplesForRegime: 20);

        result.UsedFallback.Should().BeTrue();
        result.UsedRegime.Should().Be(MarketRegime.Unknown);
        // Pooled p = 66/110 = 0.60, pooled b = 200/100 = 2.0 → Kelly 0.4 → fractioned 0.1
        result.Fraction.Should().BeApproximately(0.1, 1e-6);
    }

    [Fact]
    public void Compute_MaxFractionCap_Enforced()
    {
        // Extreme edge: p=0.95, b=10. Kelly = (0.95*10 - 0.05)/10 = 0.945 → quarter = 0.236.
        // Cap at 0.10 should clamp.
        var stats = new[]
        {
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Ranging, Wins: 95, Losses: 5, AverageWin: 1000.0, AverageLoss: 100.0)
        };
        var result = RegimeConditionedKelly.Compute(
            MarketRegime.Ranging, stats, fractionalKelly: 0.25, maxFraction: 0.10);
        result.Fraction.Should().Be(0.10);
    }

    [Fact]
    public void Compute_DifferentRegimesGiveDifferentKelly()
    {
        // Same strategy: profitable in Ranging, losing in Trending.
        var stats = new[]
        {
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Ranging, Wins: 60, Losses: 40, AverageWin: 200.0, AverageLoss: 100.0),
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Trending, Wins: 30, Losses: 70, AverageWin: 100.0, AverageLoss: 150.0)
        };

        var rangingResult = RegimeConditionedKelly.Compute(MarketRegime.Ranging, stats);
        var trendingResult = RegimeConditionedKelly.Compute(MarketRegime.Trending, stats);

        rangingResult.Fraction.Should().BeGreaterThan(0);
        trendingResult.Fraction.Should().Be(0.0,
            "Trending regime has negative EV → no position");
    }

    [Fact]
    public void Compute_FractionalKellyMultiplies()
    {
        var stats = new[]
        {
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Ranging, Wins: 60, Losses: 40, AverageWin: 200.0, AverageLoss: 100.0)
        };

        var quarter = RegimeConditionedKelly.Compute(MarketRegime.Ranging, stats, fractionalKelly: 0.25, maxFraction: 1.0);
        var half = RegimeConditionedKelly.Compute(MarketRegime.Ranging, stats, fractionalKelly: 0.50, maxFraction: 1.0);
        var full = RegimeConditionedKelly.Compute(MarketRegime.Ranging, stats, fractionalKelly: 1.00, maxFraction: 1.0);

        half.Fraction.Should().BeApproximately(2.0 * quarter.Fraction, 1e-9);
        full.Fraction.Should().BeApproximately(4.0 * quarter.Fraction, 1e-9);
    }

    [Fact]
    public void Compute_PicksLatestRegimeRow_WhenMultipleSameRegime()
    {
        // Two rows for Ranging — caller may pass aggregate snapshots with different
        // sample sizes. Compute should pick the one with higher Total (most data).
        var stats = new[]
        {
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Ranging, Wins: 5, Losses: 5, AverageWin: 100.0, AverageLoss: 100.0),
            new RegimeConditionedKelly.RegimeStats(
                MarketRegime.Ranging, Wins: 60, Losses: 40, AverageWin: 200.0, AverageLoss: 100.0)
        };
        var result = RegimeConditionedKelly.Compute(MarketRegime.Ranging, stats);

        // Should use the second row (60+40=100 samples > 5+5=10).
        result.WinRateUsed.Should().BeApproximately(0.6, 1e-9);
        result.WinLossRatioUsed.Should().BeApproximately(2.0, 1e-9);
    }
}
