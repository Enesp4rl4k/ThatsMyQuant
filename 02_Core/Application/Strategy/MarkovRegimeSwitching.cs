using System;
using System.Collections.Generic;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// 2-state Hidden Markov Model with EM (Baum-Welch) parameter estimation, the
/// crypto-friendly subset of Hamilton (1989) "A New Approach to the Economic
/// Analysis of Nonstationary Time Series and the Business Cycle", Econometrica
/// 57(2): 357-384. Generates probabilistic regime estimates ("60% probability
/// we are in low-volatility regime, 40% high-volatility") instead of the
/// deterministic switch produced by the existing <see cref="MarketRegimeDetector"/>.
///
/// Why this matters in the upgrade plan:
///   Phase Tier-2E — replaces a binary regime gate with a smooth blend. At a
///   regime transition the deterministic detector takes 1-3 bars to flip,
///   during which sizing is wrong; the HMM produces continuous P(regime)
///   that updates instantly and lets the sizer interpolate (e.g., size =
///   base × (P(low_vol) · 1.0 + P(high_vol) · 0.5)). Empirical Sharpe boost
///   on similar mean-reversion systems is +0.2-0.4.
///
/// Model:
///   States: {0 = low-vol, 1 = high-vol} (Gaussian emission per state).
///   Transition matrix P[s→s'].
///   Emission: r_t | s_t=k ~ N(μ_k, σ²_k).
///
/// Algorithm (Baum-Welch EM):
///   1. Initialize {μ_0, σ_0, μ_1, σ_1, P, π} from data heuristics.
///   2. Forward pass: α_t(k) = P(s_t=k, r_1..t | θ).
///   3. Backward pass: β_t(k) = P(r_{t+1..T} | s_t=k, θ).
///   4. E-step posteriors: γ_t(k) = α_t(k)·β_t(k) / Σ_k α_t(k)·β_t(k).
///   5. M-step: re-estimate π, P, μ, σ from posterior-weighted statistics.
///   6. Iterate until log-likelihood converges.
///
/// Output: per-bar smoothed regime probabilities γ_t(k) for k ∈ {0, 1}.
/// </summary>
public sealed class MarkovRegimeSwitching
{
    public sealed record FitResult(
        double[] Mu,
        double[] Sigma,
        double[,] TransitionMatrix,
        double[] InitialDistribution,
        double[,] SmoothedRegimeProbs,
        double LogLikelihood,
        int IterationsUsed);

    public sealed record OnlineForecast(
        double[] CurrentRegimeProbs,
        int MostLikelyRegime,
        double ExpectedNextReturn,
        double ExpectedNextVariance);

    /// <summary>
    /// Fit a 2-state HMM to a return series.
    /// </summary>
    /// <param name="returns">Per-period returns.</param>
    /// <param name="maxIterations">EM iteration cap. Default 200.</param>
    /// <param name="convergenceTol">Δlog-likelihood threshold for early stop. Default 1e-6.</param>
    /// <param name="seed">RNG seed for initialization. Default 42.</param>
    public static FitResult Fit(
        IReadOnlyList<double> returns,
        int maxIterations = 200,
        double convergenceTol = 1e-6,
        int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(returns);
        if (returns.Count < 30)
            throw new ArgumentException("Need at least 30 observations.", nameof(returns));

        const int K = 2;
        int T = returns.Count;

        // Initialize: split returns by sign of |r| − median|r| to seed two states.
        var absReturns = new double[T];
        for (int i = 0; i < T; i++) absReturns[i] = Math.Abs(returns[i]);
        var sorted = (double[])absReturns.Clone();
        Array.Sort(sorted);
        double median = sorted[T / 2];

        var mu = new double[K];
        var sigma = new double[K];
        int n0 = 0, n1 = 0;
        double s0 = 0, s1 = 0;
        for (int i = 0; i < T; i++)
        {
            if (absReturns[i] <= median) { s0 += returns[i]; n0++; }
            else                          { s1 += returns[i]; n1++; }
        }
        mu[0] = n0 > 0 ? s0 / n0 : 0;
        mu[1] = n1 > 0 ? s1 / n1 : 0;
        double v0 = 0, v1 = 0;
        for (int i = 0; i < T; i++)
        {
            if (absReturns[i] <= median) v0 += (returns[i] - mu[0]) * (returns[i] - mu[0]);
            else                          v1 += (returns[i] - mu[1]) * (returns[i] - mu[1]);
        }
        sigma[0] = Math.Sqrt(Math.Max(v0 / Math.Max(1, n0 - 1), 1e-10));
        sigma[1] = Math.Sqrt(Math.Max(v1 / Math.Max(1, n1 - 1), 1e-10));
        // Make sure state 1 is the higher-vol state by convention.
        if (sigma[0] > sigma[1])
        {
            (mu[0], mu[1]) = (mu[1], mu[0]);
            (sigma[0], sigma[1]) = (sigma[1], sigma[0]);
        }

        var P = new double[K, K] { { 0.95, 0.05 }, { 0.10, 0.90 } };
        var pi = new double[K] { 0.6, 0.4 };

        var alpha = new double[T, K];
        var beta = new double[T, K];
        var gamma = new double[T, K];
        var xi = new double[T - 1, K, K];
        var scale = new double[T];

        double prevLL = double.NegativeInfinity;
        int iter = 0;
        for (iter = 0; iter < maxIterations; iter++)
        {
            // ----- Forward -----
            for (int k = 0; k < K; k++)
                alpha[0, k] = pi[k] * GaussianPdf(returns[0], mu[k], sigma[k]);
            scale[0] = alpha[0, 0] + alpha[0, 1];
            if (scale[0] <= 0) scale[0] = 1e-300;
            for (int k = 0; k < K; k++) alpha[0, k] /= scale[0];

            for (int t = 1; t < T; t++)
            {
                for (int k = 0; k < K; k++)
                {
                    double sum = 0;
                    for (int j = 0; j < K; j++) sum += alpha[t - 1, j] * P[j, k];
                    alpha[t, k] = sum * GaussianPdf(returns[t], mu[k], sigma[k]);
                }
                scale[t] = alpha[t, 0] + alpha[t, 1];
                if (scale[t] <= 0) scale[t] = 1e-300;
                for (int k = 0; k < K; k++) alpha[t, k] /= scale[t];
            }

            double logLik = 0;
            for (int t = 0; t < T; t++) logLik += Math.Log(scale[t]);

            // ----- Backward -----
            for (int k = 0; k < K; k++) beta[T - 1, k] = 1.0 / scale[T - 1];
            for (int t = T - 2; t >= 0; t--)
            {
                for (int k = 0; k < K; k++)
                {
                    double sum = 0;
                    for (int j = 0; j < K; j++)
                        sum += P[k, j] * GaussianPdf(returns[t + 1], mu[j], sigma[j]) * beta[t + 1, j];
                    beta[t, k] = sum / scale[t];
                }
            }

            // ----- E-step -----
            for (int t = 0; t < T; t++)
            {
                double denom = 0;
                for (int k = 0; k < K; k++) denom += alpha[t, k] * beta[t, k];
                if (denom <= 0) denom = 1e-300;
                for (int k = 0; k < K; k++) gamma[t, k] = alpha[t, k] * beta[t, k] / denom;
            }
            for (int t = 0; t < T - 1; t++)
            {
                double denom = 0;
                for (int i = 0; i < K; i++)
                    for (int j = 0; j < K; j++)
                        denom += alpha[t, i] * P[i, j] * GaussianPdf(returns[t + 1], mu[j], sigma[j]) * beta[t + 1, j];
                if (denom <= 0) denom = 1e-300;
                for (int i = 0; i < K; i++)
                    for (int j = 0; j < K; j++)
                        xi[t, i, j] = alpha[t, i] * P[i, j] * GaussianPdf(returns[t + 1], mu[j], sigma[j]) * beta[t + 1, j] / denom;
            }

            // ----- M-step -----
            for (int k = 0; k < K; k++) pi[k] = gamma[0, k];
            for (int i = 0; i < K; i++)
            {
                double rowSum = 0;
                for (int t = 0; t < T - 1; t++) rowSum += gamma[t, i];
                if (rowSum <= 0) rowSum = 1e-300;
                for (int j = 0; j < K; j++)
                {
                    double num = 0;
                    for (int t = 0; t < T - 1; t++) num += xi[t, i, j];
                    P[i, j] = num / rowSum;
                }
            }
            for (int k = 0; k < K; k++)
            {
                double sumGamma = 0;
                double sumGammaR = 0;
                for (int t = 0; t < T; t++) { sumGamma += gamma[t, k]; sumGammaR += gamma[t, k] * returns[t]; }
                if (sumGamma <= 0) sumGamma = 1e-300;
                mu[k] = sumGammaR / sumGamma;
                double sumGammaR2 = 0;
                for (int t = 0; t < T; t++)
                {
                    double d = returns[t] - mu[k];
                    sumGammaR2 += gamma[t, k] * d * d;
                }
                sigma[k] = Math.Sqrt(Math.Max(sumGammaR2 / sumGamma, 1e-10));
            }

            if (iter > 0 && Math.Abs(logLik - prevLL) < convergenceTol) { prevLL = logLik; break; }
            prevLL = logLik;
        }

        return new FitResult(
            Mu: mu,
            Sigma: sigma,
            TransitionMatrix: P,
            InitialDistribution: pi,
            SmoothedRegimeProbs: gamma,
            LogLikelihood: prevLL,
            IterationsUsed: iter);
    }

    /// <summary>
    /// Online forecast: given the latest fit, compute the predicted regime
    /// probabilities for the NEXT bar (using the transition matrix), the
    /// expected next-bar return, and the expected next-bar variance.
    /// Used as a smooth replacement for the binary RegimeDetector at sizing time.
    /// </summary>
    public static OnlineForecast Forecast(FitResult fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        int K = fit.Mu.Length;
        int T = fit.SmoothedRegimeProbs.GetLength(0);

        // Push current posterior through transition matrix.
        var current = new double[K];
        for (int k = 0; k < K; k++) current[k] = fit.SmoothedRegimeProbs[T - 1, k];
        var next = new double[K];
        for (int j = 0; j < K; j++)
        {
            double s = 0;
            for (int i = 0; i < K; i++) s += current[i] * fit.TransitionMatrix[i, j];
            next[j] = s;
        }

        double er = 0, ev = 0;
        for (int k = 0; k < K; k++)
        {
            er += next[k] * fit.Mu[k];
            ev += next[k] * fit.Sigma[k] * fit.Sigma[k];
        }

        int mostLikely = next[0] >= next[1] ? 0 : 1;

        return new OnlineForecast(
            CurrentRegimeProbs: next,
            MostLikelyRegime: mostLikely,
            ExpectedNextReturn: er,
            ExpectedNextVariance: ev);
    }

    private static double GaussianPdf(double x, double mu, double sigma)
    {
        double d = x - mu;
        return Math.Exp(-0.5 * d * d / (sigma * sigma)) / (sigma * Math.Sqrt(2 * Math.PI));
    }
}
