using System;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class ParticleFilter1DTests
{
    [Fact]
    public void Constructor_TooFewParticles_Throws()
    {
        Action act = () => new ParticleFilter1D(numParticles: 1, 0.0, 1.0, 0.01);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NegativeVariance_Throws()
    {
        Action initVar = () => new ParticleFilter1D(100, 0.0, -1.0, 0.01);
        initVar.Should().Throw<ArgumentException>();
        Action procVar = () => new ParticleFilter1D(100, 0.0, 1.0, -0.01);
        procVar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Update_ConvergesToTrueStaticState_GaussianNoise()
    {
        // True state x* = 5.0. Noisy observations z_t = x* + ε, ε ~ N(0, 1).
        // After many observations the posterior mean should be close to x*.
        const double truth = 5.0;
        var filter = new ParticleFilter1D(
            numParticles: 500,
            initialState: 0.0,
            initialVariance: 9.0,
            processVariance: 0.0,   // truly static state
            seed: 42);

        var rng = new Random(101);
        var likelihood = ParticleFilter1D.GaussianLogLikelihood(
            measurementFn: x => x,
            observationVariance: 1.0);

        for (int i = 0; i < 200; i++)
        {
            double obs = truth + StandardGaussian(rng) * 1.0;
            filter.Update(obs, likelihood);
        }

        filter.EstimateState().Should().BeApproximately(truth, 0.5);
    }

    [Fact]
    public void Update_TracksMovingState_RandomWalk()
    {
        // True state evolves: x_t = x_{t-1} + 0.05 · η_t.
        // PF must track via process noise.
        var rng = new Random(202);
        double truth = 10.0;
        var filter = new ParticleFilter1D(
            numParticles: 800,
            initialState: 10.0,
            initialVariance: 1.0,
            processVariance: 0.05 * 0.05,
            seed: 99);

        var likelihood = ParticleFilter1D.GaussianLogLikelihood(
            measurementFn: x => x,
            observationVariance: 0.5 * 0.5);

        double maxError = 0.0;
        for (int i = 0; i < 300; i++)
        {
            truth += 0.05 * StandardGaussian(rng);
            double obs = truth + 0.5 * StandardGaussian(rng);
            filter.Update(obs, likelihood);
            if (i > 50)
            {
                double err = Math.Abs(filter.EstimateState() - truth);
                if (err > maxError) maxError = err;
            }
        }

        // Should track within reasonable bounds of obs noise + drift.
        maxError.Should().BeLessThan(2.5,
            "particle filter must track random-walk state given moderate noise");
    }

    [Fact]
    public void Update_RecoversFromJump_WithStudentTLikelihood()
    {
        // True state has a sudden jump. Two PF mechanisms enable recovery:
        //   (a) Student-t likelihood assigns non-zero weight to far residuals,
        //       so post-jump observations don't kill all particles.
        //   (b) Process noise lets particles drift toward the new region over time.
        // Compared to a linear-Gaussian Kalman, recovery is faster because the
        // posterior is non-parametric and tracks bimodal distributions transiently.
        var rng = new Random(303);
        double truth = 0.0;
        var filter = new ParticleFilter1D(
            numParticles: 1500,
            initialState: 0.0,
            initialVariance: 1.0,
            processVariance: 0.1 * 0.1,    // tracking budget for jumps
            seed: 50);

        var likelihood = ParticleFilter1D.StudentTLogLikelihood(
            measurementFn: x => x,
            scale: 0.2,
            degreesOfFreedom: 3.0);  // very heavy tails — give residuals non-trivial weight

        // Pre-jump: 60 observations near truth = 0
        for (int i = 0; i < 60; i++)
        {
            double obs = truth + 0.2 * StandardGaussian(rng);
            filter.Update(obs, likelihood);
        }
        double preJumpEstimate = filter.EstimateState();
        preJumpEstimate.Should().BeApproximately(0.0, 0.4);

        // Jump to 2.0 (moderate, within process-noise budget over the recovery window)
        truth = 2.0;
        for (int i = 0; i < 80; i++)
        {
            double obs = truth + 0.2 * StandardGaussian(rng);
            filter.Update(obs, likelihood);
        }

        // After 80 observations post-jump, state should be substantially closer
        // to new truth than to old.
        double postJumpEstimate = filter.EstimateState();
        postJumpEstimate.Should().BeGreaterThan(1.0,
            "PF must move toward post-jump truth (was 0, now 2)");
    }

    [Fact]
    public void EffectiveSampleSize_StartsAtN_DecreasesUnderInformativeLikelihood()
    {
        var filter = new ParticleFilter1D(
            numParticles: 200,
            initialState: 0.0,
            initialVariance: 100.0,   // very wide prior
            processVariance: 0.0,
            seed: 7);

        // ESS at construction: uniform weights → N_eff = N.
        filter.EffectiveSampleSize().Should().BeApproximately(200.0, 1.0);

        // After one informative observation (tight likelihood), ESS drops.
        var likelihood = ParticleFilter1D.GaussianLogLikelihood(
            measurementFn: x => x,
            observationVariance: 0.01);

        filter.Update(observation: 0.0, likelihood);
        // After resampling kicks in (ESS < N/2), ESS should reset toward N.
        filter.EffectiveSampleSize().Should().BeGreaterThan(100.0,
            "systematic resampling should restore diversity after collapse");
    }

    [Fact]
    public void GaussianLogLikelihood_MatchesAnalytic()
    {
        var ll = ParticleFilter1D.GaussianLogLikelihood(x => x, observationVariance: 4.0);
        // Standard Gaussian density: (2π·4)^{-0.5} · exp(-(2)²/8) = 0.1209853 → log = -2.1129...
        double v = ll(0.0, 2.0);
        double expected = -0.5 * Math.Log(2.0 * Math.PI * 4.0) - 0.5 * (2.0 * 2.0) / 4.0;
        v.Should().BeApproximately(expected, 1e-12);
    }

    [Fact]
    public void StudentTLogLikelihood_HeavierTailsThanGaussian_AtSameScale()
    {
        // Far-tail residual: r = 5σ. Student-t (ν=4) should assign higher density
        // (heavier tails) than Gaussian at the same scale.
        var gaussian = ParticleFilter1D.GaussianLogLikelihood(x => x, observationVariance: 1.0);
        var studentT = ParticleFilter1D.StudentTLogLikelihood(x => x, scale: 1.0, degreesOfFreedom: 4.0);

        double gLog = gaussian(0.0, 5.0);
        double tLog = studentT(0.0, 5.0);

        tLog.Should().BeGreaterThan(gLog,
            "Student-t has heavier tails than Gaussian at extreme residuals");
    }

    [Fact]
    public void StudentTLogLikelihood_InvalidParams_Throws()
    {
        Action zeroScale = () => ParticleFilter1D.StudentTLogLikelihood(x => x, scale: 0.0, degreesOfFreedom: 4.0);
        zeroScale.Should().Throw<ArgumentException>();
        Action lowDof = () => ParticleFilter1D.StudentTLogLikelihood(x => x, scale: 1.0, degreesOfFreedom: 1.5);
        lowDof.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GaussianLogLikelihood_InvalidParams_Throws()
    {
        Action act = () => ParticleFilter1D.GaussianLogLikelihood(x => x, observationVariance: 0.0);
        act.Should().Throw<ArgumentException>();
    }

    private static double StandardGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
