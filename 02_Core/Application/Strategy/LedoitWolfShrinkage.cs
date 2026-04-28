using System;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Ledoit-Wolf optimal linear shrinkage estimator for sample covariance matrices,
/// targeting a constant-correlation structure. Returns a regularized covariance
/// estimate that is a convex combination of the noisy sample covariance and a
/// well-conditioned target, with the mixing weight chosen by closed-form
/// asymptotic minimization of expected squared Frobenius loss.
///
/// References:
///   Ledoit &amp; Wolf (2004), "Honey, I Shrunk the Sample Covariance Matrix",
///     Journal of Portfolio Management 30(4): 110-119.
///   Ledoit &amp; Wolf (2004), "A well-conditioned estimator for large-dimensional
///     covariance matrices", Journal of Multivariate Analysis 88(2): 365-411.
///
/// Why this matters for the pro upgrade plan:
///   When N (assets) is large relative to T (observations), the sample covariance
///   matrix is ill-conditioned, with eigenvalues spread artificially wide. This
///   wrecks downstream operations that rely on the inverse — Markowitz portfolios,
///   Mahalanobis distances for residual pairs, GLS regressions. Shrinking toward a
///   constant-correlation target preserves the diagonal (sample variances are
///   well-estimated) while pulling the off-diagonal toward a stable, single-parameter
///   summary, dramatically improving condition number and out-of-sample performance.
///
///   In the upgrade plan: feeds Phase B (HRP), Phase J4 (PCA factor residuals),
///   Phase K3 (DCC-GARCH calibration), Phase K8 (Black-Litterman posterior).
///
/// Algorithm (constant-correlation target):
///   1. Sample covariance S = X' X / T (population denominator, NOT T-1).
///   2. Target F: same diagonal as S, off-diagonal = r̄ × σ_i × σ_j where r̄ is
///      the mean of off-diagonal sample correlations.
///   3. Asymptotic variance of vectorized S:    π = Σ_ij (1/T) Σ_t (x_ti·x_tj − S_ij)²
///   4. Asymptotic covariance of S and F:        ρ = π_diag + Σ_{i≠j} (r̄/2)·[√(σ_jj/σ_ii)·θ_iiij + √(σ_ii/σ_jj)·θ_jjij]
///   5. Misspecification:                        γ = ‖F − S‖²_F
///   6. Optimal shrinkage:                       δ* = max(0, min(1, (π − ρ)/(γT)))
///   7. Estimator:                               Σ̂ = δ*·F + (1−δ*)·S
///
/// Inputs are assumed to be returns (not prices). Caller is responsible for
/// taking log-returns or simple returns before invoking. The estimator centers
/// internally (subtracts column means).
/// </summary>
public static class LedoitWolfShrinkage
{
    /// <summary>
    /// Result bundle for a Ledoit-Wolf shrinkage estimation.
    /// </summary>
    /// <param name="Covariance">Shrunk covariance matrix Σ̂ = δ·F + (1−δ)·S, [N×N], symmetric, positive semi-definite.</param>
    /// <param name="Delta">Optimal shrinkage intensity δ* ∈ [0,1]. 0 = pure sample, 1 = pure target.</param>
    /// <param name="SampleCovariance">Unshrunken sample covariance S, [N×N]. Useful for diagnostics.</param>
    /// <param name="Target">Constant-correlation target F, [N×N]. Diagonal matches S; off-diagonal = r̄·σ_i·σ_j.</param>
    /// <param name="AverageCorrelation">r̄, the mean of off-diagonal sample correlations used to build F.</param>
    /// <param name="Pi">Asymptotic variance estimate π̂.</param>
    /// <param name="Rho">Asymptotic covariance estimate ρ̂ (between S and F).</param>
    /// <param name="Gamma">Squared Frobenius misspecification γ̂ = ‖F − S‖²_F.</param>
    public sealed record ShrinkageResult(
        double[,] Covariance,
        double Delta,
        double[,] SampleCovariance,
        double[,] Target,
        double AverageCorrelation,
        double Pi,
        double Rho,
        double Gamma);

    /// <summary>
    /// Estimate the Ledoit-Wolf shrunk covariance matrix from a return panel.
    /// </summary>
    /// <param name="returns">Return matrix [T×N]: T observations of N assets.
    /// Caller-supplied returns (log or simple); this method centers internally.</param>
    /// <returns>Shrinkage result bundle.</returns>
    /// <exception cref="ArgumentNullException">When returns is null.</exception>
    /// <exception cref="ArgumentException">When fewer than 2 observations or 1 asset is provided.</exception>
    public static ShrinkageResult Estimate(double[,] returns)
    {
        ArgumentNullException.ThrowIfNull(returns);

        int t = returns.GetLength(0);
        int n = returns.GetLength(1);

        if (t < 2)
            throw new ArgumentException("At least 2 observations required.", nameof(returns));
        if (n < 1)
            throw new ArgumentException("At least 1 asset required.", nameof(returns));

        // Center returns column-wise (subtract mean per asset).
        var centered = new double[t, n];
        for (int j = 0; j < n; j++)
        {
            double mean = 0.0;
            for (int i = 0; i < t; i++) mean += returns[i, j];
            mean /= t;
            for (int i = 0; i < t; i++) centered[i, j] = returns[i, j] - mean;
        }

        // Sample covariance S = X'X / T (population denominator, per Ledoit-Wolf).
        var s = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i; j < n; j++)
            {
                double dot = 0.0;
                for (int k = 0; k < t; k++) dot += centered[k, i] * centered[k, j];
                double cov = dot / t;
                s[i, j] = cov;
                s[j, i] = cov;
            }
        }

        // Sample standard deviations (square roots of diagonal).
        var sd = new double[n];
        for (int i = 0; i < n; i++) sd[i] = Math.Sqrt(Math.Max(s[i, i], 0.0));

        // Average off-diagonal correlation r̄.
        double sumCorr = 0.0;
        int countCorr = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double denom = sd[i] * sd[j];
                if (denom > 0.0)
                {
                    sumCorr += s[i, j] / denom;
                    countCorr++;
                }
            }
        }
        double rBar = countCorr > 0 ? sumCorr / countCorr : 0.0;

        // Constant-correlation target F: diag(S) preserved; off-diag = r̄·σ_i·σ_j.
        var f = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            f[i, i] = s[i, i];
            for (int j = i + 1; j < n; j++)
            {
                double off = rBar * sd[i] * sd[j];
                f[i, j] = off;
                f[j, i] = off;
            }
        }

        // π̂ = sum over (i,j) of asymptotic variance of √T·S_ij,
        //    estimated as (1/T) Σ_t (x_ti·x_tj − S_ij)².
        // Cache π_ij for use in ρ̂ off-diagonal terms.
        var piMatrix = new double[n, n];
        double piHat = 0.0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double acc = 0.0;
                for (int k = 0; k < t; k++)
                {
                    double d = centered[k, i] * centered[k, j] - s[i, j];
                    acc += d * d;
                }
                acc /= t;
                piMatrix[i, j] = acc;
                piHat += acc;
            }
        }

        // ρ̂ = diagonal contribution + off-diagonal asymptotic covariance.
        // Diagonal: ρ_diag = Σ_i π_ii.
        double rhoHat = 0.0;
        for (int i = 0; i < n; i++) rhoHat += piMatrix[i, i];

        // Off-diagonal: for each i ≠ j, contribute (r̄/2) × [(σ_j/σ_i)·θ_iiij + (σ_i/σ_j)·θ_jjij]
        //   where θ_iiij = (1/T) Σ_t (x_ti² − S_ii)·(x_ti·x_tj − S_ij)
        //         θ_jjij = (1/T) Σ_t (x_tj² − S_jj)·(x_ti·x_tj − S_ij)
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                if (sd[i] <= 0.0 || sd[j] <= 0.0) continue;

                double thetaIIij = 0.0;
                double thetaJJij = 0.0;
                for (int k = 0; k < t; k++)
                {
                    double xi = centered[k, i];
                    double xj = centered[k, j];
                    double crossDev = xi * xj - s[i, j];
                    thetaIIij += (xi * xi - s[i, i]) * crossDev;
                    thetaJJij += (xj * xj - s[j, j]) * crossDev;
                }
                thetaIIij /= t;
                thetaJJij /= t;

                rhoHat += (rBar / 2.0) * ((sd[j] / sd[i]) * thetaIIij + (sd[i] / sd[j]) * thetaJJij);
            }
        }

        // γ̂ = ‖F − S‖²_F.
        double gammaHat = 0.0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double d = f[i, j] - s[i, j];
                gammaHat += d * d;
            }
        }

        // Optimal shrinkage δ* = max(0, min(1, (π̂ − ρ̂) / (γ̂·T))).
        // When γ̂ ≈ 0 the target is identical to the sample → no shrinkage needed.
        double delta;
        if (gammaHat <= 1e-12)
        {
            delta = 0.0;
        }
        else
        {
            double kappa = (piHat - rhoHat) / gammaHat;
            delta = Math.Max(0.0, Math.Min(1.0, kappa / t));
        }

        // Σ̂ = δ·F + (1−δ)·S. Symmetric by construction (S and F both symmetric).
        var sigma = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                sigma[i, j] = delta * f[i, j] + (1.0 - delta) * s[i, j];
            }
        }

        return new ShrinkageResult(
            Covariance: sigma,
            Delta: delta,
            SampleCovariance: s,
            Target: f,
            AverageCorrelation: rBar,
            Pi: piHat,
            Rho: rhoHat,
            Gamma: gammaHat);
    }

    /// <summary>
    /// Convenience overload: takes a jagged-array return panel and converts internally.
    /// </summary>
    /// <param name="returns">Jagged array indexed as returns[t][n].</param>
    public static ShrinkageResult Estimate(double[][] returns)
    {
        ArgumentNullException.ThrowIfNull(returns);
        if (returns.Length == 0)
            throw new ArgumentException("At least 2 observations required.", nameof(returns));

        int t = returns.Length;
        int n = returns[0].Length;
        var matrix = new double[t, n];
        for (int i = 0; i < t; i++)
        {
            if (returns[i].Length != n)
                throw new ArgumentException($"Row {i} has length {returns[i].Length}, expected {n}.", nameof(returns));
            for (int j = 0; j < n; j++) matrix[i, j] = returns[i][j];
        }
        return Estimate(matrix);
    }

    /// <summary>
    /// Compute the condition number of a symmetric positive semi-definite matrix
    /// via the ratio of largest to smallest eigenvalue. Returns +∞ if the matrix is
    /// numerically singular. Used to compare shrunk vs sample covariance stability.
    /// </summary>
    /// <remarks>Uses Jacobi rotations — O(N³) but exact for small N (≤ 100). For large
    /// portfolios a Lanczos / power-iteration approach would be preferable.</remarks>
    public static double ConditionNumber(double[,] symmetricMatrix)
    {
        ArgumentNullException.ThrowIfNull(symmetricMatrix);
        int n = symmetricMatrix.GetLength(0);
        if (n == 0 || symmetricMatrix.GetLength(1) != n)
            throw new ArgumentException("Matrix must be square and non-empty.", nameof(symmetricMatrix));

        var eigenvalues = JacobiEigenvalues(symmetricMatrix);
        double minEv = double.PositiveInfinity;
        double maxEv = 0.0;
        foreach (var ev in eigenvalues)
        {
            double abs = Math.Abs(ev);
            if (abs < minEv) minEv = abs;
            if (abs > maxEv) maxEv = abs;
        }
        if (minEv <= 1e-15) return double.PositiveInfinity;
        return maxEv / minEv;
    }

    // Jacobi eigenvalue iteration for symmetric matrices. Returns eigenvalues only;
    // eigenvectors not needed for condition-number diagnostic. ~50 sweeps converges
    // to machine precision for N ≤ 50.
    private static double[] JacobiEigenvalues(double[,] m)
    {
        int n = m.GetLength(0);
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) a[i, j] = m[i, j];

        const int maxSweeps = 100;
        const double tol = 1e-12;

        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            double offDiagSum = 0.0;
            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++) offDiagSum += Math.Abs(a[p, q]);
            if (offDiagSum < tol) break;

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
                }
            }
        }

        var eigenvalues = new double[n];
        for (int i = 0; i < n; i++) eigenvalues[i] = a[i, i];
        return eigenvalues;
    }
}
