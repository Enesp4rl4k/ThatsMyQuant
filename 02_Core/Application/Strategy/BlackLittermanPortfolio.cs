using System;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Black-Litterman portfolio construction. Combines a "neutral" prior (typically
/// CAPM-implied equilibrium returns) with active investor views, weighted by the
/// confidence in each view, to produce a posterior expected-return vector. The
/// posterior is then run through Markowitz mean-variance optimization to get
/// portfolio weights.
///
/// References:
///   Black &amp; Litterman (1992), "Global Portfolio Optimization", Goldman Sachs
///     Fixed Income Research.
///   He &amp; Litterman (1999), "The Intuition Behind Black-Litterman Model
///     Portfolios", Goldman Sachs Investment Management Research.
///   Idzorek (2005), "A Step-by-Step Guide to the Black-Litterman Model".
///
/// Why Black-Litterman over Markowitz mean-variance:
///   Plain MVO is famously unstable: tiny changes in expected-return inputs
///   produce wildly different portfolios (corner solutions). B-L tames this by
///   anchoring to an equilibrium prior and only deviating in proportion to the
///   strength of the investor's stated views — producing intuitive, well-
///   behaved portfolios that respect both market consensus and active edges.
///
///   In the upgrade plan: each new alpha source (microstructure, funding flip,
///   liquidation cascade) becomes a "view" with a backtest-derived confidence.
///   The resulting portfolio reflects the strength of every edge simultaneously
///   without devolving into a 100%-into-the-best-Sharpe-trade corner.
///
/// Math (matrix form):
///   E[r] = π                                 (prior expected returns, [N×1])
///   Σ                                        (asset covariance, [N×N])
///   τ                                        (scalar; uncertainty of the prior — typically 0.025-0.10)
///   P [K×N]                                  (each row = a view: linear combo of assets)
///   Q [K×1]                                  (expected return for each view)
///   Ω [K×K]                                  (uncertainty of views; diagonal)
///
///   Posterior expected returns:
///     E_BL[r] = [(τΣ)^{-1} + P'Ω^{-1}P]^{-1} · [(τΣ)^{-1}π + P'Ω^{-1}Q]
///
///   Posterior weights (mean-variance with risk aversion λ):
///     w = (λΣ)^{-1} · E_BL[r]
///   normalized so Σ w_i = 1 (long-only versions clip negatives).
/// </summary>
public static class BlackLittermanPortfolio
{
    public sealed record BlackLittermanResult(
        double[] PosteriorExpectedReturns,
        double[] Weights,
        double[] PriorReturns,
        double TauUsed,
        double RiskAversionUsed);

    /// <summary>
    /// Compute Black-Litterman posterior returns and Markowitz weights.
    /// </summary>
    /// <param name="priorReturns">π — prior expected returns [N]. Common choice: reverse-optimization
    /// from market-cap weights, or recent historical mean returns.</param>
    /// <param name="covariance">Σ — asset covariance matrix [N×N]. Use Ledoit-Wolf for stability.</param>
    /// <param name="viewMatrix">P — views matrix [K×N]. Each row encodes one view:
    /// e.g., P[k] = (1, -1, 0, ...) means "asset 0 outperforms asset 1 by Q[k]".
    /// Pass an empty matrix [0×N] when there are no active views (returns the prior).</param>
    /// <param name="viewReturns">Q — expected returns for views [K]. Same row index as viewMatrix.</param>
    /// <param name="viewUncertainty">Ω — diagonal of view uncertainty [K]. Smaller = more confident.
    /// Heuristic: ω_k = (P[k] · Σ · P[k]') · τ / confidence, where confidence ∈ (0,1].</param>
    /// <param name="tau">τ — uncertainty of the prior. Standard 0.025-0.10. Default 0.05.</param>
    /// <param name="riskAversion">λ — Markowitz risk aversion. Higher = more conservative.
    /// 2-5 typical for institutional portfolios. Default 2.5.</param>
    /// <param name="longOnly">When true, negative posterior weights are clipped to 0
    /// before re-normalization. Crypto perp portfolios can short, so this defaults to false.</param>
    public static BlackLittermanResult Allocate(
        double[] priorReturns,
        double[,] covariance,
        double[,] viewMatrix,
        double[] viewReturns,
        double[] viewUncertainty,
        double tau = 0.05,
        double riskAversion = 2.5,
        bool longOnly = false)
    {
        ArgumentNullException.ThrowIfNull(priorReturns);
        ArgumentNullException.ThrowIfNull(covariance);
        ArgumentNullException.ThrowIfNull(viewMatrix);
        ArgumentNullException.ThrowIfNull(viewReturns);
        ArgumentNullException.ThrowIfNull(viewUncertainty);

        int n = priorReturns.Length;
        if (covariance.GetLength(0) != n || covariance.GetLength(1) != n)
            throw new ArgumentException($"Covariance must be [{n}×{n}].");
        int k = viewMatrix.GetLength(0);
        if (k > 0 && viewMatrix.GetLength(1) != n)
            throw new ArgumentException("View matrix column count must equal asset count.");
        if (viewReturns.Length != k)
            throw new ArgumentException("View returns must have length equal to view count.");
        if (viewUncertainty.Length != k)
            throw new ArgumentException("View uncertainty must have length equal to view count.");
        if (tau <= 0) throw new ArgumentOutOfRangeException(nameof(tau));
        if (riskAversion <= 0) throw new ArgumentOutOfRangeException(nameof(riskAversion));

        // No-views fast path: posterior == prior, weights from Markowitz with prior.
        if (k == 0)
        {
            var weightsPrior = MarkowitzWeights(priorReturns, covariance, riskAversion, longOnly);
            return new BlackLittermanResult(
                PosteriorExpectedReturns: (double[])priorReturns.Clone(),
                Weights: weightsPrior,
                PriorReturns: (double[])priorReturns.Clone(),
                TauUsed: tau,
                RiskAversionUsed: riskAversion);
        }

        // Build (τΣ)^{-1}
        var tauSigma = MatrixOps.Scale(covariance, tau);
        var tauSigmaInv = MatrixOps.Invert(tauSigma);

        // Build Ω^{-1} (Ω is diagonal)
        var omegaInv = new double[k, k];
        for (int i = 0; i < k; i++)
        {
            if (viewUncertainty[i] <= 0)
                throw new ArgumentException($"View uncertainty at index {i} must be positive.");
            omegaInv[i, i] = 1.0 / viewUncertainty[i];
        }

        // M = (τΣ)^{-1} + P' Ω^{-1} P
        var pT = MatrixOps.Transpose(viewMatrix);
        var pTOmegaInv = MatrixOps.Multiply(pT, omegaInv);
        var pTOmegaInvP = MatrixOps.Multiply(pTOmegaInv, viewMatrix);
        var m = MatrixOps.Add(tauSigmaInv, pTOmegaInvP);

        // RHS = (τΣ)^{-1} π + P' Ω^{-1} Q
        var tauSigmaInvPi = MatrixOps.MultiplyVector(tauSigmaInv, priorReturns);
        var pTOmegaInvQ = MatrixOps.MultiplyVector(pTOmegaInv, viewReturns);
        var rhs = new double[n];
        for (int i = 0; i < n; i++) rhs[i] = tauSigmaInvPi[i] + pTOmegaInvQ[i];

        // E_BL = M^{-1} · RHS  (use Solve for numerical stability)
        var posterior = MatrixOps.Solve(m, rhs);

        var weights = MarkowitzWeights(posterior, covariance, riskAversion, longOnly);

        return new BlackLittermanResult(
            PosteriorExpectedReturns: posterior,
            Weights: weights,
            PriorReturns: (double[])priorReturns.Clone(),
            TauUsed: tau,
            RiskAversionUsed: riskAversion);
    }

    /// <summary>
    /// Reverse-optimization to recover implied equilibrium returns from a given
    /// weight vector (e.g., market-cap weights):
    ///     π = λ · Σ · w
    /// Useful for constructing the prior in Black-Litterman.
    /// </summary>
    public static double[] ImpliedEquilibriumReturns(
        double[,] covariance,
        double[] equilibriumWeights,
        double riskAversion = 2.5)
    {
        ArgumentNullException.ThrowIfNull(covariance);
        ArgumentNullException.ThrowIfNull(equilibriumWeights);
        if (riskAversion <= 0) throw new ArgumentOutOfRangeException(nameof(riskAversion));

        int n = equilibriumWeights.Length;
        if (covariance.GetLength(0) != n || covariance.GetLength(1) != n)
            throw new ArgumentException("Covariance / weights size mismatch.");

        var sigmaW = MatrixOps.MultiplyVector(covariance, equilibriumWeights);
        var pi = new double[n];
        for (int i = 0; i < n; i++) pi[i] = riskAversion * sigmaW[i];
        return pi;
    }

    /// <summary>
    /// Heuristic for view uncertainty: ω_k = (P[k] · Σ · P[k]') · τ / confidence.
    /// confidence ∈ (0, 1] — 1.0 means "as certain as the prior", smaller means less.
    /// Idzorek (2005) Eq. (4) without his iterative-tilt complication.
    /// </summary>
    public static double[] ViewUncertaintyFromConfidence(
        double[,] viewMatrix,
        double[,] covariance,
        double[] confidences,
        double tau = 0.05)
    {
        ArgumentNullException.ThrowIfNull(viewMatrix);
        ArgumentNullException.ThrowIfNull(covariance);
        ArgumentNullException.ThrowIfNull(confidences);

        int k = viewMatrix.GetLength(0);
        int n = viewMatrix.GetLength(1);
        if (confidences.Length != k)
            throw new ArgumentException("confidences length must equal view count.");

        var omega = new double[k];
        for (int i = 0; i < k; i++)
        {
            if (confidences[i] <= 0 || confidences[i] > 1)
                throw new ArgumentException($"confidences[{i}] must be in (0, 1].");
            // ω_k = P_k · Σ · P_k' · τ / c
            double quad = 0.0;
            for (int p = 0; p < n; p++)
            {
                double row = 0.0;
                for (int q = 0; q < n; q++) row += covariance[p, q] * viewMatrix[i, q];
                quad += viewMatrix[i, p] * row;
            }
            omega[i] = quad * tau / confidences[i];
        }
        return omega;
    }

    /// <summary>
    /// Markowitz mean-variance weights: w = (λΣ)^{-1} · E[r], normalized so Σ w_i = 1.
    /// When longOnly = true, negative weights are clipped before re-normalization.
    /// </summary>
    private static double[] MarkowitzWeights(
        double[] expectedReturns,
        double[,] covariance,
        double riskAversion,
        bool longOnly)
    {
        int n = expectedReturns.Length;
        var lambdaSigma = MatrixOps.Scale(covariance, riskAversion);
        var w = MatrixOps.Solve(lambdaSigma, expectedReturns);

        if (longOnly)
        {
            for (int i = 0; i < n; i++) if (w[i] < 0) w[i] = 0.0;
        }

        // Normalize so |w| sum to 1. For long-only: regular sum; for long-short: signed sum
        // could vanish, so use sum of absolute values as a safe denominator.
        double sumAbs = 0.0;
        double sumSigned = 0.0;
        for (int i = 0; i < n; i++) { sumAbs += Math.Abs(w[i]); sumSigned += w[i]; }
        double denom = longOnly ? sumSigned : (sumAbs > 0 ? sumAbs : 1.0);
        if (denom != 0)
        {
            for (int i = 0; i < n; i++) w[i] /= denom;
        }
        return w;
    }
}
