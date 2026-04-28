using System;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// One-dimensional Kalman filter for adaptive hedge-ratio (β) estimation in
/// pairs trading. Replaces periodic OLS rebalance with per-tick Bayesian
/// updates — β drifts smoothly as the pair's relationship evolves instead of
/// jumping at rebalance boundaries.
///
/// Model:
///   state      : β_t (scalar hedge ratio, Y ≈ β · X)
///   transition : β_t = β_{t-1} + w_t,     w_t ~ N(0, Q)  — β is a random walk
///   observation: y_t = β_t · x_t + v_t,   v_t ~ N(0, R)  — spread noise
///
/// Tuning:
///   Q (process noise): how fast β is *allowed* to change. Larger Q = more
///                      responsive, more jittery. 1e-5 .. 1e-3 is typical.
///   R (observation noise): measurement variance of y given β·x. Set roughly
///                          to the spread's variance ≈ residualStd².
///
/// The filter converges from a seed β (typically OLS on an initial window)
/// and thereafter updates one scalar multiply + one add per tick — O(1).
/// </summary>
public sealed class KalmanHedgeRatioFilter
{
    private double _beta;         // posterior mean of β
    private double _betaVariance; // posterior variance of β
    private readonly double _processVariance;
    private double _observationVariance;

    /// <param name="initialBeta">Seed for β — use OLS estimate from warm-up window.</param>
    /// <param name="initialVariance">Initial uncertainty on β. Higher = faster convergence to data early on.</param>
    /// <param name="processVariance">Q — random-walk drift variance per step.</param>
    /// <param name="observationVariance">R — spread innovation variance. Updatable via <see cref="SetObservationVariance"/>.</param>
    public KalmanHedgeRatioFilter(
        double initialBeta,
        double initialVariance = 1.0,
        double processVariance = 1e-4,
        double observationVariance = 1.0)
    {
        _beta = initialBeta;
        _betaVariance = Math.Max(1e-9, initialVariance);
        _processVariance = Math.Max(0.0, processVariance);
        _observationVariance = Math.Max(1e-9, observationVariance);
    }

    public double Beta => _beta;
    public double BetaVariance => _betaVariance;

    /// <summary>
    /// Update the posterior variance R — useful when the running spread std-dev
    /// is re-estimated (e.g. at each cointegration rebalance).
    /// </summary>
    public void SetObservationVariance(double r) => _observationVariance = Math.Max(1e-9, r);

    /// <summary>
    /// Ingest an observation (y, x) and advance β.
    ///
    /// Predict: β_t|t-1 = β_{t-1|t-1}  ;  P_t|t-1 = P_{t-1|t-1} + Q
    /// Update : innovation = y - β·x  ;  S = x²·P + R
    ///          K = P·x / S
    ///          β ← β + K·innovation  ;  P ← (1 - K·x)·P
    /// </summary>
    public void Update(double y, double x)
    {
        // Predict — β is a random walk, so mean unchanged, variance grows by Q.
        double pPredicted = _betaVariance + _processVariance;

        // Update
        double innovation = y - _beta * x;
        double innovationVariance = x * x * pPredicted + _observationVariance;
        if (innovationVariance <= 0) return;

        double kalmanGain = (pPredicted * x) / innovationVariance;
        _beta = _beta + kalmanGain * innovation;
        _betaVariance = (1.0 - kalmanGain * x) * pPredicted;

        // Guard against numerical drift into negative variance.
        if (_betaVariance < 0) _betaVariance = pPredicted;
    }

    /// <summary>Reset the filter to a fresh seed β. Useful when a cointegration
    /// break is detected and the old posterior is no longer credible.</summary>
    public void Reseed(double beta, double variance = 1.0)
    {
        _beta = beta;
        _betaVariance = Math.Max(1e-9, variance);
    }
}
