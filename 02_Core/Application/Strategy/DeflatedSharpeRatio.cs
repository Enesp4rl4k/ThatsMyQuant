using System;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Deflated Sharpe Ratio (DSR) and Probabilistic Sharpe Ratio (PSR) estimators
/// per Bailey &amp; Lopez de Prado (2014), "The Deflated Sharpe Ratio: Correcting
/// for Selection Bias, Backtest Overfitting, and Non-Normality".
///
/// Why naïve Sharpe over-states real performance:
///   1. <b>Selection bias</b>: when we tune N candidate strategies and pick
///      the highest-Sharpe one, the chosen Sharpe is upward-biased even if
///      every candidate is pure noise. The bias scales with √log(N).
///   2. <b>Non-normality</b>: standard Sharpe-test t-statistics assume normal
///      returns. Real-world skewed/heavy-tailed returns make the t-test
///      anti-conservative — Sharpe-stars that look 4σ from zero may only be
///      1.5σ once kurtosis is accounted for.
///
/// PSR formula (Bailey-Lopez de Prado, eq. 9):
///   PSR(SR*) = Φ( (SR_hat − SR*) · √(T−1) / √(1 − γ_3·SR_hat + (γ_4 − 1)/4 · SR_hat²) )
///   where γ_3 = sample skewness, γ_4 = sample kurtosis (NOT excess), T = sample size.
///
/// Deflated SR threshold (eq. 10) for selection-bias correction:
///   SR* = √(Var(SR_candidates)) · ((1 − γ) Z⁻¹(1 − 1/N) + γ Z⁻¹(1 − 1/(N·e)))
///   where γ ≈ 0.5772 (Euler-Mascheroni), Z⁻¹ is the inverse standard normal CDF.
///
/// Used in upgrade plan: pre-paper-restart validation gate. ANY strategy must
/// clear DSR &gt; 0.95 (95% confidence the true Sharpe exceeds zero given the
/// number of candidates we tried) before being promoted to live.
/// </summary>
public static class DeflatedSharpeRatio
{
    /// <summary>One return-series's DSR diagnostic bundle.</summary>
    public sealed record DsrResult(
        double SharpeRatio,
        double Skewness,
        double Kurtosis,
        double SampleSize,
        double Threshold,
        double ProbabilisticSharpe,
        double DeflatedSharpe,
        bool PassesNinetyFiveConfidence);

    /// <summary>
    /// Compute the standalone Probabilistic Sharpe Ratio (no selection-bias
    /// correction — use this when you have ONE strategy, not a candidate pool).
    /// PSR(0) tests "is the true Sharpe &gt; 0?".
    /// </summary>
    /// <param name="returns">Per-period returns (already de-meaned in caller's units of choice).</param>
    /// <param name="benchmarkSharpe">SR* — null hypothesis Sharpe to test against. Default 0.</param>
    public static double ProbabilisticSharpe(IReadOnlyList<double> returns, double benchmarkSharpe = 0.0)
    {
        ArgumentNullException.ThrowIfNull(returns);
        if (returns.Count < 4)
            throw new ArgumentException("Need at least 4 observations for PSR.", nameof(returns));

        var (sharpe, skew, kurt, t) = ComputeMoments(returns);
        return PsrCore(sharpe, skew, kurt, t, benchmarkSharpe);
    }

    /// <summary>
    /// Compute the Deflated Sharpe Ratio for a return series chosen from N
    /// candidate strategies (selection-bias-corrected PSR).
    /// </summary>
    /// <param name="returns">The chosen strategy's per-period returns.</param>
    /// <param name="numTrials">N — number of candidate strategies tried (parameters / pairs / regimes).
    /// Set to 1 for no correction.</param>
    /// <param name="varianceOfTrialSharpes">Var(SR_candidates) — sample variance of all N candidates' Sharpes.
    /// Larger spread among candidates ⇒ higher selection bias.</param>
    public static DsrResult Compute(
        IReadOnlyList<double> returns,
        int numTrials,
        double varianceOfTrialSharpes)
    {
        ArgumentNullException.ThrowIfNull(returns);
        if (returns.Count < 4)
            throw new ArgumentException("Need at least 4 observations.", nameof(returns));
        if (numTrials < 1)
            throw new ArgumentOutOfRangeException(nameof(numTrials));
        if (varianceOfTrialSharpes < 0)
            throw new ArgumentOutOfRangeException(nameof(varianceOfTrialSharpes));

        var (sharpe, skew, kurt, t) = ComputeMoments(returns);

        double threshold;
        if (numTrials == 1 || varianceOfTrialSharpes <= 0)
        {
            threshold = 0.0;
        }
        else
        {
            // Bailey-Lopez de Prado expected-max-of-N formula.
            //   Z⁻¹(1 − 1/N) + γ · (Z⁻¹(1 − 1/(N·e)) − Z⁻¹(1 − 1/N))
            // multiplied by √Var(SR_candidates) gives the expected maximum Sharpe under noise.
            const double euler = 0.5772156649015329;
            double e = Math.E;
            double q1 = NormalInverseCdf(1.0 - 1.0 / numTrials);
            double q2 = NormalInverseCdf(1.0 - 1.0 / (numTrials * e));
            double expectedMax = (1 - euler) * q1 + euler * q2;
            threshold = Math.Sqrt(varianceOfTrialSharpes) * expectedMax;
        }

        double psrAtZero = PsrCore(sharpe, skew, kurt, t, 0.0);
        double dsr = PsrCore(sharpe, skew, kurt, t, threshold);

        return new DsrResult(
            SharpeRatio: sharpe,
            Skewness: skew,
            Kurtosis: kurt,
            SampleSize: t,
            Threshold: threshold,
            ProbabilisticSharpe: psrAtZero,
            DeflatedSharpe: dsr,
            PassesNinetyFiveConfidence: dsr >= 0.95);
    }

    // -------- internals --------

    private static (double Sharpe, double Skew, double Kurt, double T) ComputeMoments(IReadOnlyList<double> returns)
    {
        int n = returns.Count;
        double mean = 0;
        for (int i = 0; i < n; i++) mean += returns[i];
        mean /= n;

        double m2 = 0, m3 = 0, m4 = 0;
        for (int i = 0; i < n; i++)
        {
            double d = returns[i] - mean;
            double d2 = d * d;
            m2 += d2;
            m3 += d2 * d;
            m4 += d2 * d2;
        }
        m2 /= n; m3 /= n; m4 /= n;

        double std = Math.Sqrt(m2);
        double sharpe = std > 0 ? mean / std : 0.0;

        // Sample skew, kurt (NOT excess). γ_3 = m3 / m2^{3/2}, γ_4 = m4 / m2².
        double skew = m2 > 0 ? m3 / Math.Pow(m2, 1.5) : 0.0;
        double kurt = m2 > 0 ? m4 / (m2 * m2) : 3.0;

        return (sharpe, skew, kurt, n);
    }

    private static double PsrCore(double sharpe, double skew, double kurt, double t, double benchmark)
    {
        // PSR(SR*) = Φ( (SR − SR*) · √(T−1) / √(1 − γ_3·SR + (γ_4−1)/4 · SR²) )
        if (t < 2) return 0.0;
        double denom = 1.0 - skew * sharpe + ((kurt - 1.0) / 4.0) * sharpe * sharpe;
        if (denom <= 0) return sharpe > benchmark ? 1.0 : 0.0;
        double z = (sharpe - benchmark) * Math.Sqrt(t - 1) / Math.Sqrt(denom);
        return NormalCdf(z);
    }

    private static double NormalCdf(double z)
    {
        // High-quality erf-based approximation.
        double k = 1.0 / (1.0 + 0.2316419 * Math.Abs(z));
        double pdf = Math.Exp(-0.5 * z * z) / Math.Sqrt(2 * Math.PI);
        double poly = k * (0.319381530
                  + k * (-0.356563782
                  + k * (1.781477937
                  + k * (-1.821255978
                  + k * 1.330274429))));
        double cdf = 1.0 - pdf * poly;
        return z >= 0 ? cdf : 1.0 - cdf;
    }

    private static double NormalInverseCdf(double p)
    {
        // Beasley-Springer-Moro inverse normal. Sufficient precision for DSR thresholds.
        if (p <= 0 || p >= 1) throw new ArgumentOutOfRangeException(nameof(p), "Must be in (0,1).");
        double[] a = { -3.969683028665376e+01,  2.209460984245205e+02,
                       -2.759285104469687e+02,  1.383577518672690e+02,
                       -3.066479806614716e+01,  2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01,  1.615858368580409e+02,
                       -1.556989798598866e+02,  6.680131188771972e+01,
                       -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01,
                       -2.400758277161838e+00, -2.549732539343734e+00,
                        4.374664141464968e+00,  2.938163982698783e+00 };
        double[] d = {  7.784695709041462e-03,  3.224671290700398e-01,
                        2.445134137142996e+00,  3.754408661907416e+00 };
        const double pLow = 0.02425;
        const double pHigh = 1.0 - pLow;
        double q, r;
        if (p < pLow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0]*q + c[1])*q + c[2])*q + c[3])*q + c[4])*q + c[5]) /
                   ((((d[0]*q + d[1])*q + d[2])*q + d[3])*q + 1);
        }
        if (p <= pHigh)
        {
            q = p - 0.5;
            r = q * q;
            return (((((a[0]*r + a[1])*r + a[2])*r + a[3])*r + a[4])*r + a[5]) * q /
                   (((((b[0]*r + b[1])*r + b[2])*r + b[3])*r + b[4])*r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0]*q + c[1])*q + c[2])*q + c[3])*q + c[4])*q + c[5]) /
                ((((d[0]*q + d[1])*q + d[2])*q + d[3])*q + 1);
    }
}
