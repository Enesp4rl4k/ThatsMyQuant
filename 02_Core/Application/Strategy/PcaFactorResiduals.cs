using System;
using System.Collections.Generic;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// PCA-based statistical-factor residuals — the canonical Renaissance / AQR
/// approach to broad statistical arbitrage. From a panel of N asset returns,
/// extract the top K principal components (interpreted as latent market /
/// sector factors), regress each asset's returns on those factors, and trade
/// the residuals as N synthetic mean-reverting "pairs."
///
/// Why this beats hand-picked pairs:
///   • <b>Market-neutral by construction</b>: residuals have removed the
///     common-factor exposure (BTC beta, sector beta), so each one is purely
///     idiosyncratic. Classical pairs are only neutral within the pair's
///     hedge ratio, leaving residual portfolio beta when stacked.
///   • <b>N edges from N assets</b>: 20 admitted symbols → 20 residual series
///     to trade simultaneously, vastly more breadth than the 6 hand-picked
///     pairs in the current portfolio.
///   • <b>Adaptive</b>: the factors are re-estimated each rebalance, so the
///     "definition of market" updates as crypto sector rotation evolves.
///
/// References:
///   Lopez de Prado (2018), "Advances in Financial Machine Learning", Ch. 8.
///   Avellaneda &amp; Lee (2010), "Statistical Arbitrage in the U.S. Equities
///     Market", Quantitative Finance 10(7): 761-782 — the canonical paper for
///     the eigenportfolio residual approach.
///
/// Algorithm:
///   1. Compute the asset return covariance matrix Σ (use Ledoit-Wolf for
///      stability when N is large vs sample size).
///   2. Eigen-decompose Σ → eigenvectors V[N×K], eigenvalues λ[K] (top K).
///   3. Each asset i's exposure to factor k is V[i,k] (factor loading).
///   4. Compute factor returns: factor_t,k = Σ_i V[i,k] · returns_t,i / Σ_i V[i,k]
///   5. For each asset i, regress returns_i on the K factors → residual_i.
///   6. The standardized residual z-score = (residual − rollingMean) / rollingStd
///      is the trade signal: |z| &gt; threshold → trade against z's sign.
///
/// Used in upgrade plan: Phase J4 — central pair-coverage expansion. Inputs
/// are the same daily/hourly returns matrix Sprint 1 was built around; uses
/// LedoitWolfShrinkage for the covariance and MatrixOps for eigen-decomposition.
/// </summary>
public static class PcaFactorResiduals
{
    /// <summary>
    /// Outcome of the residual computation for a single rebalance.
    /// </summary>
    /// <param name="ResidualMatrix">[T×N] residuals: residuals[t, i] is asset i's idiosyncratic
    /// return at time t after stripping the K factors.</param>
    /// <param name="FactorLoadings">[N×K] loadings (V): how each asset weights into each factor.</param>
    /// <param name="EigenvaluesUsed">Top K eigenvalues, descending.</param>
    /// <param name="VarianceExplainedFraction">Fraction of total return variance accounted for by the K factors (0-1).</param>
    public sealed record FactorDecomposition(
        double[,] ResidualMatrix,
        double[,] FactorLoadings,
        double[] EigenvaluesUsed,
        double VarianceExplainedFraction);

    /// <summary>
    /// One residual entry-signal evaluation for a specific asset.
    /// </summary>
    public sealed record ResidualSignal(
        int AssetIndex,
        double LatestResidual,
        double ResidualMean,
        double ResidualStdDev,
        double ZScore);

    /// <summary>
    /// Decompose a returns panel into top-K factors and residuals.
    /// </summary>
    /// <param name="returns">[T×N] return matrix (T observations × N assets).</param>
    /// <param name="numFactors">K. Typical: 1 (market beta), 3 (market + 2 sector), 5+ for finer cuts.</param>
    /// <param name="useLedoitWolf">When true, regularize the covariance via Ledoit-Wolf shrinkage
    /// before eigen-decomposition. Recommended when T &lt; 5×N. Default true.</param>
    public static FactorDecomposition Decompose(
        double[,] returns,
        int numFactors,
        bool useLedoitWolf = true)
    {
        ArgumentNullException.ThrowIfNull(returns);
        int t = returns.GetLength(0);
        int n = returns.GetLength(1);
        if (t < 2 || n < 2)
            throw new ArgumentException("Need at least 2 observations and 2 assets.");
        if (numFactors < 1 || numFactors > n)
            throw new ArgumentOutOfRangeException(nameof(numFactors), $"Must be in [1, {n}].");

        // 1. Covariance.
        double[,] cov;
        if (useLedoitWolf)
        {
            var lw = LedoitWolfShrinkage.Estimate(returns);
            cov = lw.Covariance;
        }
        else
        {
            cov = SampleCovariance(returns);
        }

        // 2. Eigen-decomposition (Jacobi).
        var (eigenvalues, eigenvectors) = JacobiEigen(cov);

        // Order eigenvalues descending.
        var indexedEigs = new (double Value, int Index)[n];
        for (int i = 0; i < n; i++) indexedEigs[i] = (eigenvalues[i], i);
        Array.Sort(indexedEigs, (a, b) => b.Value.CompareTo(a.Value));

        // 3. Top-K eigenvectors.
        var loadings = new double[n, numFactors];
        var topEigs = new double[numFactors];
        double totalEig = 0.0;
        for (int i = 0; i < n; i++) totalEig += Math.Max(0.0, eigenvalues[i]);
        double topEigSum = 0.0;
        for (int k = 0; k < numFactors; k++)
        {
            int colIdx = indexedEigs[k].Index;
            topEigs[k] = indexedEigs[k].Value;
            topEigSum += Math.Max(0.0, indexedEigs[k].Value);
            for (int i = 0; i < n; i++) loadings[i, k] = eigenvectors[i, colIdx];
        }
        double varianceExplained = totalEig > 0 ? topEigSum / totalEig : 0.0;

        // 4. Center returns (subtract column means).
        var centered = new double[t, n];
        var means = new double[n];
        for (int j = 0; j < n; j++)
        {
            double sum = 0;
            for (int i = 0; i < t; i++) sum += returns[i, j];
            means[j] = sum / t;
            for (int i = 0; i < t; i++) centered[i, j] = returns[i, j] - means[j];
        }

        // 5. Factor returns: f_t,k = Σ_i loadings[i,k] · centered[t,i].
        var factorReturns = new double[t, numFactors];
        for (int i = 0; i < t; i++)
        {
            for (int k = 0; k < numFactors; k++)
            {
                double s = 0;
                for (int j = 0; j < n; j++) s += loadings[j, k] * centered[i, j];
                factorReturns[i, k] = s;
            }
        }

        // 6. Residuals: residual_t,i = centered_t,i − Σ_k loadings[i,k] · factor_t,k.
        var residuals = new double[t, n];
        for (int i = 0; i < t; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double predicted = 0;
                for (int k = 0; k < numFactors; k++)
                    predicted += loadings[j, k] * factorReturns[i, k];
                residuals[i, j] = centered[i, j] - predicted;
            }
        }

        return new FactorDecomposition(
            ResidualMatrix: residuals,
            FactorLoadings: loadings,
            EigenvaluesUsed: topEigs,
            VarianceExplainedFraction: varianceExplained);
    }

    /// <summary>
    /// Compute the latest residual z-score for each asset using the cumulative
    /// residual (sum of residual returns) — Avellaneda-Lee style: when residuals
    /// are close to mean-reverting the cumulative residual is OU-like and its
    /// z-score is the entry signal.
    /// </summary>
    public static IReadOnlyList<ResidualSignal> ComputeSignals(FactorDecomposition decomposition)
    {
        ArgumentNullException.ThrowIfNull(decomposition);
        int t = decomposition.ResidualMatrix.GetLength(0);
        int n = decomposition.ResidualMatrix.GetLength(1);
        if (t < 2) return Array.Empty<ResidualSignal>();

        // Cumulative residual per asset: sum residuals[0..t-1, i] = signal series.
        var signals = new List<ResidualSignal>(n);
        for (int j = 0; j < n; j++)
        {
            var cum = new double[t];
            cum[0] = decomposition.ResidualMatrix[0, j];
            for (int i = 1; i < t; i++)
                cum[i] = cum[i - 1] + decomposition.ResidualMatrix[i, j];

            double mean = 0;
            for (int i = 0; i < t; i++) mean += cum[i];
            mean /= t;
            double var = 0;
            for (int i = 0; i < t; i++)
            {
                double d = cum[i] - mean;
                var += d * d;
            }
            double std = Math.Sqrt(var / Math.Max(1, t - 1));

            double latest = cum[t - 1];
            double z = std > 0 ? (latest - mean) / std : 0.0;
            signals.Add(new ResidualSignal(
                AssetIndex: j,
                LatestResidual: latest,
                ResidualMean: mean,
                ResidualStdDev: std,
                ZScore: z));
        }
        return signals;
    }

    // ----- internals -----

    private static double[,] SampleCovariance(double[,] returns)
    {
        int t = returns.GetLength(0);
        int n = returns.GetLength(1);
        var means = new double[n];
        for (int j = 0; j < n; j++)
        {
            double s = 0;
            for (int i = 0; i < t; i++) s += returns[i, j];
            means[j] = s / t;
        }
        var cov = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i; j < n; j++)
            {
                double s = 0;
                for (int k = 0; k < t; k++)
                    s += (returns[k, i] - means[i]) * (returns[k, j] - means[j]);
                cov[i, j] = cov[j, i] = s / Math.Max(1, t - 1);
            }
        }
        return cov;
    }

    /// <summary>
    /// Jacobi eigenvalue/eigenvector decomposition for symmetric matrices.
    /// Returns (eigenvalues, eigenvectors as columns).
    /// </summary>
    private static (double[] Eigenvalues, double[,] Eigenvectors) JacobiEigen(double[,] m)
    {
        int n = m.GetLength(0);
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) a[i, j] = m[i, j];

        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1.0;

        const int maxSweeps = 100;
        const double tol = 1e-12;
        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            double offDiag = 0.0;
            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++) offDiag += Math.Abs(a[p, q]);
            if (offDiag < tol) break;

            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = a[p, q];
                    if (Math.Abs(apq) < tol) continue;

                    double app = a[p, p];
                    double aqq = a[q, q];
                    double theta = (aqq - app) / (2.0 * apq);
                    double tTan = theta >= 0
                        ? 1.0 / (theta + Math.Sqrt(1.0 + theta * theta))
                        : 1.0 / (theta - Math.Sqrt(1.0 + theta * theta));
                    double cCos = 1.0 / Math.Sqrt(1.0 + tTan * tTan);
                    double sSin = tTan * cCos;

                    a[p, p] = app - tTan * apq;
                    a[q, q] = aqq + tTan * apq;
                    a[p, q] = 0.0;
                    a[q, p] = 0.0;

                    for (int i = 0; i < n; i++)
                    {
                        if (i == p || i == q) continue;
                        double aip = a[i, p];
                        double aiq = a[i, q];
                        a[i, p] = cCos * aip - sSin * aiq;
                        a[p, i] = a[i, p];
                        a[i, q] = sSin * aip + cCos * aiq;
                        a[q, i] = a[i, q];
                    }
                    for (int i = 0; i < n; i++)
                    {
                        double vip = v[i, p];
                        double viq = v[i, q];
                        v[i, p] = cCos * vip - sSin * viq;
                        v[i, q] = sSin * vip + cCos * viq;
                    }
                }
            }
        }

        var eigenvalues = new double[n];
        for (int i = 0; i < n; i++) eigenvalues[i] = a[i, i];
        return (eigenvalues, v);
    }
}
