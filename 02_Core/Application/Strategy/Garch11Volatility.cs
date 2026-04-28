using System;
using System.Collections.Generic;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// GARCH(1,1) volatility model — Bollerslev (1986). Models the conditional
/// variance of returns as
///     σ²_t = ω + α · r²_{t-1} + β · σ²_{t-1}
/// where ω, α, β ≥ 0 and α + β &lt; 1 for stationarity.
///
/// Why this matters for the upgrade plan:
///   Phase K3 / K4 — DCC-GARCH dynamic correlations require univariate GARCH
///   per asset as a preprocessing step. Almgren-Chriss optimal execution uses
///   GARCH-forecasted vol to size the trade trajectory. Phase H tail hedge
///   uses GARCH to size weekly OTM put notional.
///
///   Standalone use: forecast next-bar vol for any asset, feed into
///   VolatilityTargetingService for forward-looking position sizing instead
///   of the lag-1 realized-vol approximation we currently use.
///
/// Estimation: maximum-likelihood via L-BFGS or simple grid search. This
/// implementation uses a coarse-to-fine random-restart grid since the
/// parameter space is small (3D) and the dataset typical (a few hundred bars).
/// </summary>
public sealed class Garch11Volatility
{
    /// <summary>Estimated parameters and goodness-of-fit diagnostics.</summary>
    /// <param name="Omega">Long-run variance term ω.</param>
    /// <param name="Alpha">News (squared-return) coefficient.</param>
    /// <param name="Beta">Persistence (lagged variance) coefficient.</param>
    /// <param name="LogLikelihood">Log-likelihood of the fitted model.</param>
    /// <param name="UnconditionalVariance">ω / (1 − α − β); the long-run mean of σ².</param>
    public sealed record FitResult(
        double Omega,
        double Alpha,
        double Beta,
        double LogLikelihood,
        double UnconditionalVariance);

    /// <summary>
    /// Fit GARCH(1,1) parameters to a return series via grid + local search.
    /// </summary>
    /// <param name="returns">Centered returns (caller-supplied; subtract mean if needed).</param>
    /// <param name="gridSteps">Granularity of the initial grid search per dimension. Default 12.</param>
    /// <param name="seed">RNG seed for the local-search restarts. Default 42.</param>
    public static FitResult Fit(IReadOnlyList<double> returns, int gridSteps = 12, int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(returns);
        if (returns.Count < 30)
            throw new ArgumentException("Need at least 30 observations.", nameof(returns));
        if (gridSteps < 4)
            throw new ArgumentOutOfRangeException(nameof(gridSteps));

        // Sample variance — used to seed ω.
        double mean = 0;
        for (int i = 0; i < returns.Count; i++) mean += returns[i];
        mean /= returns.Count;
        double sampleVar = 0;
        for (int i = 0; i < returns.Count; i++)
        {
            double d = returns[i] - mean;
            sampleVar += d * d;
        }
        sampleVar /= Math.Max(1, returns.Count - 1);

        // 1. Coarse grid search.
        double bestOmega = sampleVar * 0.05;
        double bestAlpha = 0.1;
        double bestBeta = 0.85;
        double bestLL = LogLikelihood(returns, bestOmega, bestAlpha, bestBeta);

        double[] alphaGrid = LinSpace(0.02, 0.20, gridSteps);
        double[] betaGrid = LinSpace(0.60, 0.96, gridSteps);
        double[] omegaGrid = LinSpace(sampleVar * 0.005, sampleVar * 0.20, gridSteps);

        foreach (var omega in omegaGrid)
        {
            foreach (var alpha in alphaGrid)
            {
                foreach (var beta in betaGrid)
                {
                    if (alpha + beta >= 0.999) continue;  // non-stationary
                    double ll = LogLikelihood(returns, omega, alpha, beta);
                    if (ll > bestLL)
                    {
                        bestLL = ll;
                        bestOmega = omega;
                        bestAlpha = alpha;
                        bestBeta = beta;
                    }
                }
            }
        }

        // 2. Local random search around the best grid point.
        var rng = new Random(seed);
        double scale = 0.03;
        for (int iter = 0; iter < 200; iter++)
        {
            double oTry = Math.Max(1e-12, bestOmega * (1.0 + (rng.NextDouble() - 0.5) * scale));
            double aTry = Math.Clamp(bestAlpha + (rng.NextDouble() - 0.5) * scale * 0.1, 0.001, 0.5);
            double bTry = Math.Clamp(bestBeta + (rng.NextDouble() - 0.5) * scale * 0.1, 0.5, 0.998);
            if (aTry + bTry >= 0.999) continue;

            double ll = LogLikelihood(returns, oTry, aTry, bTry);
            if (ll > bestLL)
            {
                bestLL = ll;
                bestOmega = oTry;
                bestAlpha = aTry;
                bestBeta = bTry;
                scale *= 0.95;  // contract on improvement
            }
            else if (iter % 50 == 0)
            {
                scale *= 0.5;   // periodic contraction
            }
        }

        double sumAB = bestAlpha + bestBeta;
        double unconditionalVar = sumAB < 1.0 ? bestOmega / (1.0 - sumAB) : sampleVar;

        return new FitResult(
            Omega: bestOmega,
            Alpha: bestAlpha,
            Beta: bestBeta,
            LogLikelihood: bestLL,
            UnconditionalVariance: unconditionalVar);
    }

    /// <summary>
    /// Forecast next-bar conditional variance given the most recent return and
    /// the most recent variance estimate.
    /// </summary>
    public static double ForecastNextVariance(double lastReturn, double lastVariance, FitResult fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        return fit.Omega + fit.Alpha * (lastReturn * lastReturn) + fit.Beta * lastVariance;
    }

    /// <summary>
    /// Run the GARCH recursion on the full return series and return the
    /// conditional-variance trajectory.
    /// </summary>
    public static double[] ConditionalVariances(IReadOnlyList<double> returns, FitResult fit)
    {
        ArgumentNullException.ThrowIfNull(returns);
        ArgumentNullException.ThrowIfNull(fit);
        int t = returns.Count;
        var sigma2 = new double[t];
        sigma2[0] = fit.UnconditionalVariance;
        for (int i = 1; i < t; i++)
        {
            sigma2[i] = fit.Omega + fit.Alpha * (returns[i - 1] * returns[i - 1]) + fit.Beta * sigma2[i - 1];
        }
        return sigma2;
    }

    private static double LogLikelihood(IReadOnlyList<double> returns, double omega, double alpha, double beta)
    {
        if (omega <= 0 || alpha < 0 || beta < 0 || alpha + beta >= 0.999) return double.NegativeInfinity;

        int t = returns.Count;
        double sigma2 = omega / Math.Max(1e-12, 1.0 - alpha - beta);
        double ll = 0;
        for (int i = 0; i < t; i++)
        {
            if (i > 0)
                sigma2 = omega + alpha * (returns[i - 1] * returns[i - 1]) + beta * sigma2;
            if (sigma2 <= 0) return double.NegativeInfinity;
            // Gaussian log-density: -0.5 · (log(2π σ²) + r²/σ²).
            ll += -0.5 * (Math.Log(2.0 * Math.PI * sigma2) + (returns[i] * returns[i]) / sigma2);
        }
        return ll;
    }

    private static double[] LinSpace(double a, double b, int n)
    {
        var arr = new double[n];
        for (int i = 0; i < n; i++) arr[i] = a + (b - a) * i / (n - 1);
        return arr;
    }
}
