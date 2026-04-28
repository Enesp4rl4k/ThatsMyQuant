using System;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Sequential Monte Carlo (Particle Filter) for 1D state estimation under
/// arbitrary, possibly non-Gaussian observation likelihoods. Drop-in replacement
/// for the linear-Gaussian Kalman filter when the noise distribution has fat
/// tails or jumps — both of which are routine in crypto markets (LunaLike, FTX).
///
/// References:
///   Doucet, de Freitas, Gordon (2001), "Sequential Monte Carlo Methods in Practice".
///   Arulampalam et al. (2002), "A Tutorial on Particle Filters for Online
///     Nonlinear/Non-Gaussian Bayesian Tracking", IEEE Trans. Signal Processing.
///
/// Why this matters for the upgrade plan:
///   The existing KalmanHedgeRatioFilter assumes ε ~ N(0, σ²). Crypto returns
///   exhibit kurtosis 6-15 (vs Gaussian's 3) and jump behavior that the Kalman
///   variance update under-estimates. A jump kicks the Kalman state into a
///   wrong basin and recovery takes many bars. The particle filter, by contrast,
///   maintains a non-parametric posterior that simply re-weights toward the
///   correct region after a jump — typical recovery is 1-3 observations.
///
///   Used in the upgrade plan: Phase K7 — robust hedge ratio re-calibration after
///   shocks. Optional drop-in for KalmanHedgeRatioFilter.
///
/// Algorithm (sequential importance resampling, SIR):
///   1. Initialize N particles {x_i}_{i=1..N} ~ N(x₀, P₀), uniform weights.
///   2. For each observation z_t with caller-supplied log-likelihood ℓ(x; z):
///      a. Propagate: x_i ← x_i + √Q · η_i,  η_i ~ N(0,1)        (state evolution)
///      b. Re-weight: log w_i ← log w_i + ℓ(x_i; z_t)             (Bayes update)
///      c. Normalize w (in log space, then exp with max-shift for stability).
///      d. Compute effective sample size N_eff = 1 / Σ w_i².
///      e. If N_eff &lt; N/2, systematic resample.
///   3. State estimate: x̂ = Σ w_i · x_i.
///
/// State model (default): random walk x_t = x_{t-1} + ε,  ε ~ N(0, Q).
/// Observation model: caller-supplied log p(z | x) — pass any distribution.
/// </summary>
public sealed class ParticleFilter1D
{
    private readonly double[] _particles;
    private readonly double[] _weights;
    private readonly Random _rng;
    private readonly double _processVariance;
    private readonly int _resampleThreshold;
    private double _spareGaussian;
    private bool _hasSpareGaussian;

    /// <summary>
    /// Construct a particle filter with N particles drawn from N(initialState, initialVariance).
    /// </summary>
    /// <param name="numParticles">Particle count. 200-1000 typical for 1D problems.</param>
    /// <param name="initialState">Prior mean.</param>
    /// <param name="initialVariance">Prior variance (uncertainty of initial state).</param>
    /// <param name="processVariance">Q — state random-walk variance per step.</param>
    /// <param name="seed">RNG seed for deterministic tests; pass 0 for default.</param>
    public ParticleFilter1D(
        int numParticles,
        double initialState,
        double initialVariance,
        double processVariance,
        int seed = 0)
    {
        if (numParticles < 2)
            throw new ArgumentException("Need at least 2 particles.", nameof(numParticles));
        if (initialVariance < 0.0)
            throw new ArgumentException("Initial variance must be non-negative.", nameof(initialVariance));
        if (processVariance < 0.0)
            throw new ArgumentException("Process variance must be non-negative.", nameof(processVariance));

        _particles = new double[numParticles];
        _weights = new double[numParticles];
        _rng = seed == 0 ? new Random() : new Random(seed);
        _processVariance = processVariance;
        _resampleThreshold = numParticles / 2;

        double sigma = Math.Sqrt(initialVariance);
        double uniform = 1.0 / numParticles;
        for (int i = 0; i < numParticles; i++)
        {
            _particles[i] = initialState + sigma * NextGaussian();
            _weights[i] = uniform;
        }
    }

    /// <summary>Number of particles.</summary>
    public int ParticleCount => _particles.Length;

    /// <summary>
    /// One Bayesian update step.
    ///   1. Propagate particles by random walk with std √Q.
    ///   2. Re-weight by caller-supplied log-likelihood ℓ(x; z).
    ///   3. Normalize.
    ///   4. Resample if effective sample size falls below half the population.
    /// </summary>
    /// <param name="observation">Observation z_t at this timestep.</param>
    /// <param name="logLikelihood">log p(z | x) — caller chooses noise model.
    /// Common choices:
    ///   • Gaussian: -0.5·log(2π·R) - 0.5·(z−h(x))²/R
    ///   • Student-t (ν dof): scaled, with heavier tails than Gaussian
    ///   • Mixture: w·Gaussian + (1−w)·heavy-tail (jump diffusion proxy)
    /// </param>
    public void Update(double observation, Func<double, double, double> logLikelihood)
    {
        ArgumentNullException.ThrowIfNull(logLikelihood);

        // 1. Propagate (random walk).
        if (_processVariance > 0.0)
        {
            double sigmaQ = Math.Sqrt(_processVariance);
            for (int i = 0; i < _particles.Length; i++)
            {
                _particles[i] += sigmaQ * NextGaussian();
            }
        }

        // 2. Re-weight in log space (avoid underflow), find max for stable softmax.
        double maxLogW = double.NegativeInfinity;
        var logWeights = new double[_weights.Length];
        for (int i = 0; i < _particles.Length; i++)
        {
            double prior = _weights[i] > 0 ? Math.Log(_weights[i]) : -1e300;
            logWeights[i] = prior + logLikelihood(_particles[i], observation);
            if (logWeights[i] > maxLogW) maxLogW = logWeights[i];
        }

        // 3. Normalize via log-sum-exp.
        double sum = 0.0;
        if (double.IsNegativeInfinity(maxLogW))
        {
            // Degenerate: all likelihoods are zero. Reset to uniform.
            double uniform = 1.0 / _weights.Length;
            for (int i = 0; i < _weights.Length; i++) _weights[i] = uniform;
            return;
        }
        for (int i = 0; i < _weights.Length; i++)
        {
            _weights[i] = Math.Exp(logWeights[i] - maxLogW);
            sum += _weights[i];
        }
        if (sum <= 0)
        {
            double uniform = 1.0 / _weights.Length;
            for (int i = 0; i < _weights.Length; i++) _weights[i] = uniform;
            return;
        }
        double inv = 1.0 / sum;
        for (int i = 0; i < _weights.Length; i++) _weights[i] *= inv;

        // 4. Effective sample size; resample if degenerate.
        double sumSq = 0.0;
        for (int i = 0; i < _weights.Length; i++) sumSq += _weights[i] * _weights[i];
        double nEff = sumSq > 0 ? 1.0 / sumSq : 0.0;

        if (nEff < _resampleThreshold) ResampleSystematic();
    }

    /// <summary>Posterior mean estimator.</summary>
    public double EstimateState()
    {
        double sum = 0.0;
        for (int i = 0; i < _particles.Length; i++) sum += _weights[i] * _particles[i];
        return sum;
    }

    /// <summary>Posterior variance estimator.</summary>
    public double EstimateVariance()
    {
        double mean = EstimateState();
        double v = 0.0;
        for (int i = 0; i < _particles.Length; i++)
        {
            double dev = _particles[i] - mean;
            v += _weights[i] * dev * dev;
        }
        return v;
    }

    /// <summary>
    /// Effective sample size N_eff = 1 / Σ w_i². Range [1, N]. Low values
    /// indicate weight degeneracy and that resampling will fire on the next update.
    /// </summary>
    public double EffectiveSampleSize()
    {
        double sumSq = 0.0;
        for (int i = 0; i < _weights.Length; i++) sumSq += _weights[i] * _weights[i];
        return sumSq > 0 ? 1.0 / sumSq : 0.0;
    }

    /// <summary>
    /// Standard Gaussian log-likelihood — convenience constructor for the
    /// default caller use case (replacing linear-Gaussian Kalman). Caller still
    /// supplies the measurement function h(x) (often h(x) = β·x).
    /// </summary>
    /// <param name="measurementFn">h(x) — predicted observation given state.</param>
    /// <param name="observationVariance">R — observation noise variance.</param>
    public static Func<double, double, double> GaussianLogLikelihood(
        Func<double, double> measurementFn,
        double observationVariance)
    {
        ArgumentNullException.ThrowIfNull(measurementFn);
        if (observationVariance <= 0.0)
            throw new ArgumentException("Observation variance must be positive.", nameof(observationVariance));
        double logNorm = -0.5 * Math.Log(2.0 * Math.PI * observationVariance);
        double invTwoR = -0.5 / observationVariance;
        return (x, z) =>
        {
            double r = z - measurementFn(x);
            return logNorm + invTwoR * r * r;
        };
    }

    /// <summary>
    /// Student-t log-likelihood with degrees-of-freedom ν. Heavier tails than
    /// Gaussian — preferred for crypto observation noise. ν = 4 is a reasonable
    /// default capturing kurtosis ≈ 6 (vs Gaussian 3).
    /// </summary>
    /// <param name="measurementFn">h(x).</param>
    /// <param name="scale">Scale parameter (analogous to σ for Gaussian).</param>
    /// <param name="degreesOfFreedom">ν, must be &gt; 2.</param>
    public static Func<double, double, double> StudentTLogLikelihood(
        Func<double, double> measurementFn,
        double scale,
        double degreesOfFreedom)
    {
        ArgumentNullException.ThrowIfNull(measurementFn);
        if (scale <= 0.0)
            throw new ArgumentException("Scale must be positive.", nameof(scale));
        if (degreesOfFreedom <= 2.0)
            throw new ArgumentException("Degrees of freedom must be > 2.", nameof(degreesOfFreedom));

        double v = degreesOfFreedom;
        double logScale = Math.Log(scale);
        // Constant: log Γ((ν+1)/2) − log Γ(ν/2) − 0.5·log(νπ) − log(scale)
        double cnst = LogGamma((v + 1.0) / 2.0) - LogGamma(v / 2.0)
                      - 0.5 * Math.Log(v * Math.PI) - logScale;
        return (x, z) =>
        {
            double r = (z - measurementFn(x)) / scale;
            return cnst - ((v + 1.0) / 2.0) * Math.Log(1.0 + (r * r) / v);
        };
    }

    private void ResampleSystematic()
    {
        // Systematic resampling: low variance, O(N).
        int n = _particles.Length;
        var fresh = new double[n];
        double u0 = _rng.NextDouble() / n;
        double cum = _weights[0];
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            double u = u0 + (double)i / n;
            while (cum < u && j < n - 1)
            {
                j++;
                cum += _weights[j];
            }
            fresh[i] = _particles[j];
        }
        Array.Copy(fresh, _particles, n);
        double uniform = 1.0 / n;
        for (int i = 0; i < n; i++) _weights[i] = uniform;
    }

    private double NextGaussian()
    {
        // Box-Muller with cached spare for efficiency.
        if (_hasSpareGaussian)
        {
            _hasSpareGaussian = false;
            return _spareGaussian;
        }
        double u1, u2;
        do { u1 = _rng.NextDouble(); } while (u1 <= double.Epsilon);
        u2 = _rng.NextDouble();
        double mag = Math.Sqrt(-2.0 * Math.Log(u1));
        double z0 = mag * Math.Cos(2.0 * Math.PI * u2);
        double z1 = mag * Math.Sin(2.0 * Math.PI * u2);
        _spareGaussian = z1;
        _hasSpareGaussian = true;
        return z0;
    }

    // Lanczos approximation to log Γ; sufficient precision for likelihoods.
    private static double LogGamma(double x)
    {
        double[] g = { 76.18009172947146, -86.50532032941677, 24.01409824083091,
                      -1.231739572450155, 0.001208650973866179, -0.000005395239384953 };
        double y = x;
        double tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        for (int j = 0; j < 6; j++)
        {
            y += 1.0;
            ser += g[j] / y;
        }
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }
}
