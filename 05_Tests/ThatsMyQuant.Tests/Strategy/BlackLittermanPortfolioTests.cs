using System;
using System.Linq;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class BlackLittermanPortfolioTests
{
    [Fact]
    public void Allocate_NoViews_PosteriorEqualsPrior()
    {
        // K = 0 → posterior should match prior verbatim.
        var prior = new double[] { 0.05, 0.07, 0.04 };
        var cov = new double[,]
        {
            { 0.04, 0.01, 0.005 },
            { 0.01, 0.06, 0.012 },
            { 0.005, 0.012, 0.05 }
        };
        var emptyView = new double[0, 3];
        var emptyQ = Array.Empty<double>();
        var emptyOmega = Array.Empty<double>();

        var result = BlackLittermanPortfolio.Allocate(prior, cov, emptyView, emptyQ, emptyOmega);

        for (int i = 0; i < 3; i++)
            result.PosteriorExpectedReturns[i].Should().BeApproximately(prior[i], 1e-12);
    }

    [Fact]
    public void Allocate_StrongView_PullsPosteriorTowardView()
    {
        // Two assets, prior says 5% / 5%. Strong view: asset 0 outperforms by 4%.
        // Posterior should tilt asset 0 above asset 1.
        var prior = new double[] { 0.05, 0.05 };
        var cov = new double[,] { { 0.04, 0.0 }, { 0.0, 0.04 } };
        var p = new double[,] { { 1.0, -1.0 } };
        var q = new double[] { 0.04 };
        // Very confident view: small uncertainty.
        var omega = new double[] { 1e-6 };

        var result = BlackLittermanPortfolio.Allocate(prior, cov, p, q, omega, tau: 0.05);

        // Posterior expected returns must satisfy E[r_0] > E[r_1].
        result.PosteriorExpectedReturns[0].Should().BeGreaterThan(result.PosteriorExpectedReturns[1]);
        // The spread should be close to 0.04 (strong view).
        (result.PosteriorExpectedReturns[0] - result.PosteriorExpectedReturns[1])
            .Should().BeApproximately(0.04, 0.005);
    }

    [Fact]
    public void Allocate_WeakView_PosteriorClosenToPrior()
    {
        // Same view, but very weak (large omega). Posterior should barely move from prior.
        var prior = new double[] { 0.05, 0.05 };
        var cov = new double[,] { { 0.04, 0.0 }, { 0.0, 0.04 } };
        var p = new double[,] { { 1.0, -1.0 } };
        var q = new double[] { 0.04 };
        var omega = new double[] { 100.0 };  // Huge uncertainty.

        var result = BlackLittermanPortfolio.Allocate(prior, cov, p, q, omega);

        result.PosteriorExpectedReturns[0].Should().BeApproximately(0.05, 0.005);
        result.PosteriorExpectedReturns[1].Should().BeApproximately(0.05, 0.005);
    }

    [Fact]
    public void Allocate_LongOnly_NegativeWeightsClipped()
    {
        // Strongly negative view on asset 1 should produce negative weight there
        // unless long-only is enforced.
        var prior = new double[] { 0.05, 0.05 };
        var cov = new double[,] { { 0.04, 0.0 }, { 0.0, 0.04 } };
        // View: asset 0 outperforms asset 1 by 30% (extreme).
        var p = new double[,] { { 1.0, -1.0 } };
        var q = new double[] { 0.30 };
        var omega = new double[] { 1e-5 };

        var resultLongOnly = BlackLittermanPortfolio.Allocate(
            prior, cov, p, q, omega, longOnly: true);
        var resultLongShort = BlackLittermanPortfolio.Allocate(
            prior, cov, p, q, omega, longOnly: false);

        // Long-only should have non-negative weights; long-short can go negative.
        resultLongOnly.Weights.All(w => w >= 0).Should().BeTrue();
        resultLongShort.Weights.Any(w => w < 0).Should().BeTrue("long-short should permit negatives");
    }

    [Fact]
    public void Allocate_DimensionMismatch_Throws()
    {
        var prior = new double[] { 0.05, 0.05 };
        var cov = new double[,] { { 0.04 } };  // 1×1 — wrong size
        var p = new double[0, 2];
        Action act = () => BlackLittermanPortfolio.Allocate(prior, cov, p, Array.Empty<double>(), Array.Empty<double>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImpliedEquilibriumReturns_RecoversInputUnderRoundtrip()
    {
        // π = λ Σ w. Then if we solve back: w = (λΣ)^{-1} π, we should recover w.
        var rng = new Random(13);
        int n = 4;
        var cov = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                cov[i, j] = i == j ? 0.04 + rng.NextDouble() * 0.02
                                   : (rng.NextDouble() - 0.5) * 0.005;
            }
            // Symmetrize.
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                cov[j, i] = cov[i, j];

        var w = new double[] { 0.4, 0.3, 0.2, 0.1 };
        double lambda = 2.5;

        var pi = BlackLittermanPortfolio.ImpliedEquilibriumReturns(cov, w, lambda);
        var lambdaSigma = MatrixOps.Scale(cov, lambda);
        var wRecovered = MatrixOps.Solve(lambdaSigma, pi);

        for (int i = 0; i < n; i++)
            wRecovered[i].Should().BeApproximately(w[i], 1e-9);
    }

    [Fact]
    public void ViewUncertaintyFromConfidence_HighConfidence_LowOmega()
    {
        var cov = new double[,]
        {
            { 0.04, 0.01 },
            { 0.01, 0.04 }
        };
        var p = new double[,] { { 1.0, -1.0 } };

        var omegaHigh = BlackLittermanPortfolio.ViewUncertaintyFromConfidence(p, cov, new double[] { 0.95 });
        var omegaLow = BlackLittermanPortfolio.ViewUncertaintyFromConfidence(p, cov, new double[] { 0.10 });

        omegaHigh[0].Should().BeLessThan(omegaLow[0],
            "higher confidence implies tighter view uncertainty");
    }

    [Fact]
    public void ViewUncertaintyFromConfidence_OutOfRange_Throws()
    {
        var cov = new double[,] { { 0.04, 0.0 }, { 0.0, 0.04 } };
        var p = new double[,] { { 1.0, -1.0 } };
        Action zero = () => BlackLittermanPortfolio.ViewUncertaintyFromConfidence(p, cov, new double[] { 0.0 });
        zero.Should().Throw<ArgumentException>();
        Action over = () => BlackLittermanPortfolio.ViewUncertaintyFromConfidence(p, cov, new double[] { 1.5 });
        over.Should().Throw<ArgumentException>();
    }
}
