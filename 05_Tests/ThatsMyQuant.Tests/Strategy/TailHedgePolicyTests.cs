using System;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class TailHedgePolicyTests
{
    [Fact]
    public void Evaluate_LowStress_NoHedge()
    {
        // Calm regime: no funding pressure, vol below baseline.
        var d = TailHedgePolicy.Evaluate(
            portfolioValueUsd: 100_000m,
            spotPrice: 50_000m,
            averageFundingBpsPer8h: 0.5,
            realizedVol30d: 0.50,
            longRunRealizedVol: 0.60);

        d.ShouldHedge.Should().BeFalse();
        d.NotionalUsd.Should().Be(0m);
        d.StressScore.Should().BeLessThan(1.0);
    }

    [Fact]
    public void Evaluate_HighFundingHighVol_FiresHedge()
    {
        // Stress regime: funding 8bps, vol 2× baseline.
        var d = TailHedgePolicy.Evaluate(
            portfolioValueUsd: 100_000m,
            spotPrice: 50_000m,
            averageFundingBpsPer8h: 8.0,
            realizedVol30d: 1.20,
            longRunRealizedVol: 0.60);

        d.ShouldHedge.Should().BeTrue();
        d.NotionalUsd.Should().BeGreaterThan(0m);
        d.StrikePrice.Should().BeApproximately(45_000m, 1m);
        d.TimeToExpiry.Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void Evaluate_NotionalScalesWithStress()
    {
        var mild = TailHedgePolicy.Evaluate(
            portfolioValueUsd: 100_000m,
            spotPrice: 50_000m,
            averageFundingBpsPer8h: 5.5,
            realizedVol30d: 0.65,
            longRunRealizedVol: 0.60);

        var severe = TailHedgePolicy.Evaluate(
            portfolioValueUsd: 100_000m,
            spotPrice: 50_000m,
            averageFundingBpsPer8h: 12.0,
            realizedVol30d: 1.50,
            longRunRealizedVol: 0.60);

        severe.ShouldHedge.Should().BeTrue();
        if (mild.ShouldHedge)
        {
            severe.NotionalUsd.Should().BeGreaterThan(mild.NotionalUsd);
        }
    }

    [Fact]
    public void Evaluate_NotionalCappedAt12Percent()
    {
        // Extreme stress should cap at 12% portfolio.
        var d = TailHedgePolicy.Evaluate(
            portfolioValueUsd: 100_000m,
            spotPrice: 50_000m,
            averageFundingBpsPer8h: 30.0,
            realizedVol30d: 2.00,
            longRunRealizedVol: 0.60);

        d.NotionalUsd.Should().BeLessThanOrEqualTo(12_000m);
    }

    [Fact]
    public void Evaluate_StrikeIsOtm_BelowSpot()
    {
        var d = TailHedgePolicy.Evaluate(
            portfolioValueUsd: 100_000m, spotPrice: 50_000m,
            averageFundingBpsPer8h: 6.0, realizedVol30d: 1.0,
            strikeOtmFraction: 0.15m);

        d.StrikePrice.Should().BeLessThan(50_000m);
        d.StrikePrice.Should().BeApproximately(50_000m * 0.85m, 0.1m);
    }

    [Fact]
    public void Evaluate_InvalidParams_Throws()
    {
        Action negPortfolio = () => TailHedgePolicy.Evaluate(-1m, 50_000m, 0, 0.5);
        negPortfolio.Should().Throw<ArgumentOutOfRangeException>();

        Action zeroSpot = () => TailHedgePolicy.Evaluate(100_000m, 0m, 0, 0.5);
        zeroSpot.Should().Throw<ArgumentOutOfRangeException>();

        Action badStrike = () => TailHedgePolicy.Evaluate(
            100_000m, 50_000m, 0, 0.5, strikeOtmFraction: 1.5m);
        badStrike.Should().Throw<ArgumentOutOfRangeException>();
    }
}
