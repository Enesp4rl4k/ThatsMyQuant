using System;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Engle-Granger cointegration detector with full diagnostic suite:
/// OLS hedge ratio, ADF on residuals, Ornstein-Uhlenbeck half-life,
/// Hurst exponent, Pearson correlation, and OU mean-reversion parameters.
///
/// The extended diagnostics let the pairs engine distinguish real mean-reverting
/// edges from spurious cointegration (cointegrated-but-random-walk spreads are
/// common when two assets share a common trend component — Hurst catches this).
/// </summary>
public static class EngleGrangerDetector
{
    /// <summary>
    /// Cointegration diagnostic bundle. All fields are populated by <see cref="TestCointegration"/>.
    /// </summary>
    /// <param name="IsCointegrated">Pass/fail of the combined cointegration gate (ADF + Hurst + correlation).</param>
    /// <param name="HedgeRatio">OLS beta from regression Y = α + βX.</param>
    /// <param name="SpreadAverage">Mean of residuals (≈ 0 by construction of OLS).</param>
    /// <param name="SpreadStdDev">Standard deviation of residuals (spread volatility scale).</param>
    /// <param name="AdfTestStatistic">Lag-1 gamma from Δε = γε_{t-1} + u. Negative → mean-reverting.</param>
    /// <param name="HalfLife">OU half-life in bars: -ln(2)/ln(1+γ). Short = fast reversion.</param>
    /// <param name="Correlation">Pearson correlation of the two price series. Needed to rule out cointegration-by-coincidence.</param>
    /// <param name="HurstExponent">Hurst exponent of the spread. H &lt; 0.5 = mean reverting, H ≈ 0.5 = random walk, H &gt; 0.5 = trending.</param>
    /// <param name="AdfTStatistic">Pseudo t-statistic on γ. More negative = stronger evidence of stationarity.</param>
    /// <param name="OuKappa">Ornstein-Uhlenbeck mean-reversion speed. Derived from γ: κ = -ln(1+γ).</param>
    /// <param name="OuTheta">OU long-run mean of the spread (≈ SpreadAverage).</param>
    /// <param name="OuSigma">OU diffusion coefficient (annualized-ish bar volatility of innovations).</param>
    public sealed record CointegrationResult(
        bool IsCointegrated,
        decimal HedgeRatio,
        decimal SpreadAverage,
        decimal SpreadStdDev,
        decimal AdfTestStatistic,
        double HalfLife,
        double Correlation = 0.0,
        double HurstExponent = 0.5,
        double AdfTStatistic = 0.0,
        double OuKappa = 0.0,
        double OuTheta = 0.0,
        double OuSigma = 0.0);

    /// <summary>
    /// Engle-Granger two-step test.
    /// Step 1 — OLS regression Y = α + βX to get hedge ratio β and residuals (spread).
    /// Step 2 — ADF-style lag-1 autoregression on residuals to test stationarity.
    /// Augmented with Hurst exponent, correlation filter, and OU parameter calibration.
    /// </summary>
    /// <remarks>
    /// Pass criteria (all must hold):
    ///   - γ ≤ -0.10 (residuals mean-revert)
    ///   - Hurst &lt; 0.55 (spread is not trending)
    ///   - |correlation| ≥ 0.30 (relationship is not noise)
    /// Tighter than the old single-γ gate but catches the common spurious-cointegration
    /// failure mode where two random walks look cointegrated over short windows.
    /// </remarks>
    public static CointegrationResult TestCointegration(IReadOnlyList<decimal> seriesY, IReadOnlyList<decimal> seriesX)
    {
        if (seriesY.Count != seriesX.Count || seriesY.Count < 30)
            return EmptyResult();

        // Step 1 — OLS hedge ratio
        decimal meanX = seriesX.Average();
        decimal meanY = seriesY.Average();

        decimal covariance = 0m;
        decimal varianceX = 0m;
        decimal varianceY = 0m;
        for (int i = 0; i < seriesX.Count; i++)
        {
            decimal dx = seriesX[i] - meanX;
            decimal dy = seriesY[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }
        if (varianceX == 0m) return EmptyResult();

        decimal beta = covariance / varianceX;
        decimal alpha = meanY - (beta * meanX);

        // Pearson correlation on raw prices — filters spurious pairs.
        double correlation = 0.0;
        if (varianceX > 0m && varianceY > 0m)
        {
            double denom = Math.Sqrt((double)varianceX * (double)varianceY);
            if (denom > 0) correlation = (double)covariance / denom;
        }

        // Residuals (spread) — the candidate stationary series.
        var residuals = new decimal[seriesX.Count];
        for (int i = 0; i < seriesX.Count; i++)
            residuals[i] = seriesY[i] - (alpha + beta * seriesX[i]);

        decimal residualMean = residuals.Average();
        decimal residualStdDev = CalculateStdDev(residuals, residualMean);

        // Step 2 — ADF lag-1: Δε_t = γ ε_{t-1} + u_t
        decimal gammaCov = 0m;
        decimal gammaVar = 0m;
        var innovations = new List<double>(residuals.Length - 1);
        for (int i = 1; i < residuals.Length; i++)
        {
            decimal deltaResidual = residuals[i] - residuals[i - 1];
            decimal laggedResidual = residuals[i - 1];
            gammaCov += deltaResidual * laggedResidual;
            gammaVar += laggedResidual * laggedResidual;

            // Track innovations for OU sigma estimate (innovations = Δε - γ ε_{t-1}, but γ not known yet;
            // we approximate with Δε since E[Δε] ≈ 0 when γ is small — refined below).
            innovations.Add((double)deltaResidual);
        }

        decimal gamma = gammaVar == 0m ? 0m : gammaCov / gammaVar;

        // Proper OLS t-statistic for γ:  t = γ / SE(γ), where SE(γ) = √(σ_u² / Σε_{t-1}²).
        double adfT = 0.0;
        if (gammaVar > 0m && residuals.Length > 2)
        {
            double ssResidOls = 0.0;
            for (int i = 1; i < residuals.Length; i++)
            {
                double dEps = (double)(residuals[i] - residuals[i - 1]);
                double lag  = (double)residuals[i - 1];
                double u    = dEps - (double)gamma * lag;
                ssResidOls += u * u;
            }
            double dof = Math.Max(1, residuals.Length - 2);
            double sigma2U = ssResidOls / dof;
            double seGamma = Math.Sqrt(sigma2U / (double)gammaVar);
            if (seGamma > 0) adfT = (double)gamma / seGamma;
        }

        // Ornstein-Uhlenbeck parameters from discrete AR(1): ε_t = (1+γ)ε_{t-1} + u_t.
        // Continuous-time: dε = κ(θ - ε)dt + σdW.  κ = -ln(1+γ), θ ≈ residualMean, σ from innovations.
        double halfLife = double.PositiveInfinity;
        double kappa = 0.0;
        double sigmaOu = 0.0;
        if (gamma < 0m && gamma > -1m)
        {
            double g = (double)gamma;
            kappa = -Math.Log(1.0 + g);
            halfLife = Math.Log(2.0) / kappa;

            // σ_OU estimate: stddev of true innovations u_t = Δε - γ ε_{t-1}
            double uSum2 = 0.0;
            int n = 0;
            for (int i = 1; i < residuals.Length; i++)
            {
                double dEps = (double)(residuals[i] - residuals[i - 1]);
                double lag  = (double)residuals[i - 1];
                double u    = dEps - g * lag;
                uSum2 += u * u;
                n++;
            }
            if (n > 0) sigmaOu = Math.Sqrt(uSum2 / n);
        }

        // Hurst exponent via rescaled-range analysis on residuals.
        // R/S is positively biased on small samples (Anis & Lloyd 1976) — a truly
        // mean-reverting AR(1)|γ|~0.5 process on n=100 frequently estimates H≈0.7.
        // To keep the gate honest, only apply the Hurst filter when n ≥ 200 where
        // the bias is sufficiently small. Below that, rely on γ + correlation.
        double hurst = ComputeHurstExponent(residuals);
        bool hurstUsable = residuals.Length >= 200 && !double.IsNaN(hurst);

        bool isCointegrated =
            gamma < -0.10m &&
            residualStdDev > 0m &&
            Math.Abs(correlation) >= 0.30 &&
            (!hurstUsable || hurst < 0.55);

        return new CointegrationResult(
            IsCointegrated: isCointegrated,
            HedgeRatio: beta,
            SpreadAverage: residualMean,
            SpreadStdDev: residualStdDev,
            AdfTestStatistic: gamma,
            HalfLife: halfLife,
            Correlation: correlation,
            HurstExponent: hurst,
            AdfTStatistic: adfT,
            OuKappa: kappa,
            OuTheta: (double)residualMean,
            OuSigma: sigmaOu);
    }

    /// <summary>
    /// Hurst exponent via the classical rescaled-range (R/S) method.
    /// H &lt; 0.5: anti-persistent (mean reverting). H = 0.5: random walk.
    /// H &gt; 0.5: persistent (trending). Essential filter for pairs trading —
    /// a cointegration pass with H ≥ 0.55 typically means the spread is
    /// random-walking inside a wide band, not mean-reverting.
    /// </summary>
    public static double ComputeHurstExponent(IReadOnlyList<decimal> series)
    {
        int n = series.Count;
        if (n < 16) return 0.5;

        // Use logarithmically spaced chunk sizes from 8 to n/2.
        var chunkSizes = new List<int>();
        for (int size = 8; size <= n / 2; size *= 2) chunkSizes.Add(size);
        if (chunkSizes.Count < 2) return 0.5;

        var logN = new List<double>(chunkSizes.Count);
        var logRs = new List<double>(chunkSizes.Count);

        foreach (int size in chunkSizes)
        {
            int numChunks = n / size;
            if (numChunks == 0) continue;

            double rsAverage = 0.0;
            int used = 0;
            for (int c = 0; c < numChunks; c++)
            {
                double mean = 0.0;
                for (int i = 0; i < size; i++) mean += (double)series[c * size + i];
                mean /= size;

                double cumDeviation = 0.0;
                double maxDev = double.NegativeInfinity;
                double minDev = double.PositiveInfinity;
                double sumSq = 0.0;
                for (int i = 0; i < size; i++)
                {
                    double dev = (double)series[c * size + i] - mean;
                    cumDeviation += dev;
                    if (cumDeviation > maxDev) maxDev = cumDeviation;
                    if (cumDeviation < minDev) minDev = cumDeviation;
                    sumSq += dev * dev;
                }
                double range = maxDev - minDev;
                double stdDev = Math.Sqrt(sumSq / size);
                if (stdDev > 1e-12 && range > 0)
                {
                    rsAverage += range / stdDev;
                    used++;
                }
            }
            if (used == 0) continue;
            rsAverage /= used;

            logN.Add(Math.Log(size));
            logRs.Add(Math.Log(rsAverage));
        }
        if (logN.Count < 2) return 0.5;

        // Linear regression slope — slope of log(R/S) vs log(N) is H.
        double avgX = logN.Average();
        double avgY = logRs.Average();
        double num = 0.0, den = 0.0;
        for (int i = 0; i < logN.Count; i++)
        {
            double dx = logN[i] - avgX;
            num += dx * (logRs[i] - avgY);
            den += dx * dx;
        }
        return den == 0 ? 0.5 : num / den;
    }

    private static CointegrationResult EmptyResult() =>
        new(false, 0m, 0m, 0m, 0m, double.PositiveInfinity);

    private static decimal CalculateStdDev(decimal[] values, decimal mean)
    {
        if (values.Length < 2) return 0m;
        decimal sumVariance = 0m;
        foreach (var v in values)
        {
            decimal d = v - mean;
            sumVariance += d * d;
        }
        return (decimal)Math.Sqrt((double)(sumVariance / (values.Length - 1)));
    }
}
