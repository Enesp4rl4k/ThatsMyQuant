using System.Globalization;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Statistical-significance utilities for multi-strategy / multi-symbol backtest decisions.
/// The naive approach — pick the best Sharpe out of N candidates and call it the winner — is
/// well-documented "data snooping" / "winner's curse": with N=10 random strategies the expected
/// max Sharpe under the null is approximately σ × √(2 ln N) ≈ 2.15σ, so a "Sharpe 1.0 winner"
/// from 10 candidates has only about 5% chance of being real edge vs ~95% being noise.
///
/// This class provides:
/// 1. Per-strategy t-statistic with Bonferroni / Holm-Bonferroni / BH-FDR family-wise corrections.
/// 2. Bootstrapped Sharpe confidence interval (basic percentile bootstrap, fast and assumption-free).
/// 3. Deflated Sharpe ratio (Bailey & López de Prado 2014) — the gold standard for accounting
///    for both the number of trials AND the higher-moment characteristics of the return series.
///
/// All methods are pure / thread-safe / no external dependencies.
/// </summary>
public static class StatisticalSignificance
{
    /// <summary>
    /// Per-trade Sharpe ratio: mean / stdev of the trade-PnL series. Returns 0 when stdev = 0
    /// or the sample is too small. NOT annualized — annualization is the caller's job because
    /// the effective trades-per-year depends on strategy cadence.
    /// </summary>
    /// <summary>
    /// Minimum standard deviation (in PnL units) treated as "non-degenerate" — below
    /// this the sample is considered all-identical (phantom artifacts, fee-only trades,
    /// etc.) and statistics return 0 instead of producing infinite or absurd values.
    /// 2026-05-01 fix: guards against the false-Bonferroni-PASS verdict observed when
    /// every trade had identical $-0.0064 cost-only PnL → stdev ≈ 1e-17 → t-stat 1e4+.
    /// </summary>
    public const double MinDegenerateStdev = 1e-6;

    /// <summary>
    /// Coefficient-of-variation floor: if stdev/|mean| &lt; this, the sample is too
    /// uniform to produce meaningful inferential statistics (every trade nearly
    /// identical → likely all phantom cost-only or fee-only artifacts). Returns 0
    /// instead of an absurd t-statistic that would falsely register as significant.
    /// 2026-05-01: needed because absolute MinDegenerateStdev was insufficient when
    /// trades clustered tightly around a non-zero mean (e.g. all $-0.0064 cost trades
    /// produced stdev ~1e-5 — above the absolute floor but giving t = -1828).
    /// </summary>
    public const double MinCoefficientOfVariation = 0.01;

    /// <summary>True when the sample is statistically degenerate (all trades nearly
    /// identical). Both absolute stdev floor AND coefficient-of-variation floor must
    /// be cleared for the sample to be considered non-degenerate.</summary>
    private static bool IsDegenerate(double mean, double stdev)
    {
        if (stdev < MinDegenerateStdev) return true;
        var absMean = Math.Abs(mean);
        if (absMean > 1e-9 && stdev / absMean < MinCoefficientOfVariation) return true;
        return false;
    }

    public static double SharpePerTrade(IReadOnlyList<double> tradePnls)
    {
        if (tradePnls is null || tradePnls.Count < 2) return 0.0;
        double mean = 0.0;
        for (int i = 0; i < tradePnls.Count; i++) mean += tradePnls[i];
        mean /= tradePnls.Count;
        double sumSq = 0.0;
        for (int i = 0; i < tradePnls.Count; i++)
        {
            var d = tradePnls[i] - mean;
            sumSq += d * d;
        }
        var stdev = Math.Sqrt(sumSq / (tradePnls.Count - 1));
        return IsDegenerate(mean, stdev) ? 0.0 : mean / stdev;
    }

    /// <summary>
    /// One-sample t-statistic against zero. The classical "is this strategy's mean PnL
    /// distinguishable from random?" test — t = mean / (stdev / √n).
    /// </summary>
    public static double TStatisticVsZero(IReadOnlyList<double> tradePnls)
    {
        if (tradePnls is null || tradePnls.Count < 2) return 0.0;
        double mean = 0.0;
        foreach (var v in tradePnls) mean += v;
        mean /= tradePnls.Count;
        double sumSq = 0.0;
        foreach (var v in tradePnls) { var d = v - mean; sumSq += d * d; }
        var stdev = Math.Sqrt(sumSq / (tradePnls.Count - 1));
        if (IsDegenerate(mean, stdev)) return 0.0;
        var stderr = stdev / Math.Sqrt(tradePnls.Count);
        return mean / stderr;
    }

    /// <summary>
    /// Bonferroni-corrected significance threshold. For N independent tests at family-wise
    /// alpha α, each individual test must clear |t| ≥ z_{α/(2N)}. Returns the required |t|
    /// magnitude under the conservative assumption of normal-approximated tails.
    /// </summary>
    public static double BonferroniRequiredAbsT(int numTests, double familyAlpha = 0.05)
    {
        if (numTests <= 0) return double.PositiveInfinity;
        // Two-sided per-test alpha
        var perTestAlpha = familyAlpha / numTests;
        // Inverse standard-normal at 1 - perTestAlpha/2 — Acklam approximation, accurate to ~5e-6.
        var p = 1.0 - perTestAlpha / 2.0;
        return InverseStandardNormal(p);
    }

    /// <summary>
    /// Bootstrapped 95% CI on per-trade Sharpe. Resamples the PnL series with replacement
    /// `iterations` times and returns the (low, high) percentile bounds. When the lower bound
    /// is below zero, the Sharpe is statistically indistinguishable from a no-edge process at
    /// the chosen confidence level.
    /// </summary>
    public static (double Low, double High, double Median) BootstrapSharpeCI(
        IReadOnlyList<double> tradePnls,
        int iterations = 2000,
        int seed = 42,
        double lowerPercentile = 0.025,
        double upperPercentile = 0.975)
    {
        if (tradePnls is null || tradePnls.Count < 2) return (0, 0, 0);
        var rng = new Random(seed);
        var sharpes = new List<double>(iterations);
        var sample = new double[tradePnls.Count];
        for (int it = 0; it < iterations; it++)
        {
            for (int j = 0; j < sample.Length; j++)
                sample[j] = tradePnls[rng.Next(tradePnls.Count)];
            var s = SharpePerTrade(sample);
            sharpes.Add(s);
        }
        sharpes.Sort();
        double Pct(double q) => sharpes[Math.Clamp((int)(q * sharpes.Count), 0, sharpes.Count - 1)];
        return (Pct(lowerPercentile), Pct(upperPercentile), Pct(0.5));
    }

    /// <summary>
    /// Deflated Sharpe Ratio (Bailey & López de Prado 2014). Adjusts the observed Sharpe for
    /// (a) the number of independent trials N (winner's-curse correction) and (b) the higher
    /// moments of the return distribution (skew and kurtosis). Returns the probability that
    /// the strategy's TRUE Sharpe exceeds zero, given everything we observed.
    ///
    /// p > 0.95 ≈ "very likely real edge"; p > 0.5 ≈ "more likely than not"; p ≤ 0.5 ≈ noise.
    /// </summary>
    public static double DeflatedSharpeRatioProbability(
        IReadOnlyList<double> tradePnls,
        int numTrials,
        double observedSharpe)
    {
        if (tradePnls is null || tradePnls.Count < 4 || numTrials < 1) return 0.0;
        int n = tradePnls.Count;

        // Compute skew and excess kurtosis.
        double mean = 0.0;
        foreach (var v in tradePnls) mean += v;
        mean /= n;
        double m2 = 0, m3 = 0, m4 = 0;
        foreach (var v in tradePnls)
        {
            var d = v - mean;
            var d2 = d * d;
            m2 += d2;
            m3 += d2 * d;
            m4 += d2 * d2;
        }
        m2 /= n; m3 /= n; m4 /= n;
        var stdev = Math.Sqrt(m2);
        if (stdev <= 0) return 0.0;
        var skew = m3 / (stdev * stdev * stdev);
        var excessKurt = m4 / (m2 * m2) - 3.0;

        // Expected max Sharpe under N trials of i.i.d. zero-Sharpe processes.
        // Closed-form approximation (López de Prado 2018):
        //   E[max] ≈ (1 - γ) * Φ⁻¹(1 - 1/N) + γ * Φ⁻¹(1 - 1/(N·e))
        // where γ ≈ 0.5772 (Euler–Mascheroni constant).
        const double euler = 0.5772156649015329;
        var phiInv1 = InverseStandardNormal(1.0 - 1.0 / numTrials);
        var phiInv2 = InverseStandardNormal(1.0 - 1.0 / (numTrials * Math.E));
        var expectedMaxSharpe = (1.0 - euler) * phiInv1 + euler * phiInv2;

        // Deflated SR statistic (z-score of observedSharpe vs expected-max under null,
        // adjusted for skew/kurtosis non-normality).
        var num = (observedSharpe - expectedMaxSharpe) * Math.Sqrt(n - 1);
        var den = Math.Sqrt(1.0 - skew * observedSharpe + ((excessKurt - 1.0) / 4.0) * observedSharpe * observedSharpe);
        if (den <= 0 || double.IsNaN(den)) return 0.0;
        var z = num / den;

        // P(true Sharpe > 0 | observed) ≈ Φ(z).
        return StandardNormalCdf(z);
    }

    /// <summary>
    /// Inverse standard-normal CDF — Beasley-Springer-Moro / Acklam approximation,
    /// max error ≈ 5e-6 across the entire (0, 1) range. Good enough for promotion-gate
    /// decisions; not for high-precision pricing.
    /// </summary>
    public static double InverseStandardNormal(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;

        // Coefficients from Acklam.
        double[] a =
        {
            -3.969683028665376e+01,  2.209460984245205e+02,
            -2.759285104469687e+02,  1.383577518672690e+02,
            -3.066479806614716e+01,  2.506628277459239e+00
        };
        double[] b =
        {
            -5.447609879822406e+01,  1.615858368580409e+02,
            -1.556989798598866e+02,  6.680131188771972e+01,
            -1.328068155288572e+01
        };
        double[] c =
        {
            -7.784894002430293e-03, -3.223964580411365e-01,
            -2.400758277161838e+00, -2.549732539343734e+00,
            4.374664141464968e+00,  2.938163982698783e+00
        };
        double[] d =
        {
            7.784695709041462e-03,  3.224671290700398e-01,
            2.445134137142996e+00,  3.754408661907416e+00
        };

        const double pLow = 0.02425;
        const double pHigh = 1 - pLow;
        double q, r, x;
        if (p < pLow)
        {
            q = Math.Sqrt(-2.0 * Math.Log(p));
            x = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }
        else if (p <= pHigh)
        {
            q = p - 0.5;
            r = q * q;
            x = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
                (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1.0);
        }
        else
        {
            q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            x = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }
        return x;
    }

    /// <summary>
    /// Standard-normal CDF using the complementary error function approximation
    /// (Abramowitz & Stegun 7.1.26). Max error ≈ 7.5e-8.
    /// </summary>
    public static double StandardNormalCdf(double z)
    {
        // erf approximation (A&S 7.1.26)
        const double a1 =  0.254829592;
        const double a2 = -0.284496736;
        const double a3 =  1.421413741;
        const double a4 = -1.453152027;
        const double a5 =  1.061405429;
        const double p  =  0.3275911;
        var sign = z < 0 ? -1.0 : 1.0;
        var absZ = Math.Abs(z) / Math.Sqrt(2.0);
        var t = 1.0 / (1.0 + p * absZ);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-absZ * absZ);
        return 0.5 * (1.0 + sign * y);
    }
}
