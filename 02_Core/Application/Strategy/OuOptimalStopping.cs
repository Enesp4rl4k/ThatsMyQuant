using System;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Optimal-stopping rules for an Ornstein-Uhlenbeck (OU) mean-reverting spread,
/// converting the heuristic "TP at 0.3σ / SL at 3.0σ" pair-exit policy into
/// decisions grounded in the OU dynamics that the spread actually follows.
///
/// Background:
///   The spread between cointegrated assets is modeled as an OU process
///     dz_t = -κ z_t dt + σ dW_t   (mean = 0, half-life h = ln(2)/κ)
///   so the conditional expected z-score after τ bars is
///     E[z_{t+τ} | z_t] = z_t · 2^(-τ/h)
///   and after the same τ bars the unconditional variance contribution from
///   noise is σ_z² · (1 − 2^(-2τ/h)).
///
///   These dynamics give us two operationally useful quantities:
///     1. <b>Expected mean-reversion progress</b> over the next bar:
///           progress_per_bar(z, h) = |z| · (1 − 2^(-1/h))
///     2. <b>Bars-to-target</b> at the heuristic exit z*:
///           bars(z, z*, h) = h · log₂(|z| / |z*|)
///
/// Why this matters:
///   The 2026-04-24 asymmetric-exit upgrade improved theoretical RR from 1.0 to
///   1.7 by tightening TP. But TP/SL were still constants, ignoring half-life.
///   A pair with h = 5 reverts fast — holding for the 0.3σ target costs little
///   time. A pair with h = 50 reverts slowly — holding to the same target may
///   bleed cost (carry, drift, drawdown variance) longer than the edge can pay.
///
///   This module adds two principled exits on top of TP/SL:
///     • <see cref="ShouldExitForNegativeRemainingEdge"/> — exit when the
///       remaining z-distance no longer covers cost, even with full reversion.
///     • <see cref="ShouldExitForTimeBudget"/> — exit when realized holding time
///       exceeds the OU-implied "expected-completion" budget (e.g., 2× h),
///       beating the existing fixed MaxHoldingBars without trimming fast pairs.
///
///   Used in upgrade plan: Phase B3 — replaces / augments PairsBacktestRunner exit
///   logic with these analytic gates, evaluated each bar alongside the existing
///   TP/SL/half-life-multiplier checks.
/// </summary>
public static class OuOptimalStopping
{
    /// <summary>
    /// Expected absolute reversion progress over one bar, given current |z| and
    /// half-life h:  progress = |z| · (1 − 2^{-1/h}).
    /// </summary>
    public static double ExpectedReversionPerBar(double absZScore, double halfLifeBars)
    {
        if (halfLifeBars <= 0 || double.IsInfinity(halfLifeBars) || double.IsNaN(halfLifeBars))
            return 0.0;
        return Math.Abs(absZScore) * (1.0 - Math.Pow(2.0, -1.0 / halfLifeBars));
    }

    /// <summary>
    /// Expected bars to reach the target z* from current z given half-life h:
    /// bars = h · log₂(|z| / |z*|).
    /// Returns +∞ if already inside the target band or if half-life is invalid.
    /// </summary>
    public static double ExpectedBarsToTarget(double currentZScore, double targetAbsZ, double halfLifeBars)
    {
        double absZ = Math.Abs(currentZScore);
        double tgt = Math.Max(1e-6, Math.Abs(targetAbsZ));
        if (absZ <= tgt) return 0.0;
        if (halfLifeBars <= 0 || double.IsInfinity(halfLifeBars) || double.IsNaN(halfLifeBars))
            return double.PositiveInfinity;
        return halfLifeBars * Math.Log(absZ / tgt) / Math.Log(2.0);
    }

    /// <summary>
    /// [F2] Exit when remaining z-distance no longer covers transaction cost — even
    /// if the trade reaches its TP target perfectly.
    ///
    /// Math (sigma-normalized):
    ///   costInSigmas = roundTripCostFraction / spreadStdDev
    ///   exit if (|z| − targetAbsZ) &lt; costInSigmas
    ///
    /// The previous implementation routed cost through refPrice (leg-A close),
    /// which crushed remaining edge on small-cap pairs (LINK/UNI, TRX/XLM): tiny
    /// σ_spread relative to refPrice made an 8bp round-trip cost look like the
    /// entire remaining alpha and forced premature exit. Comparing cost directly
    /// to spread σ gives a dimensionally-clean gate that doesn't depend on the
    /// asset's price scale.
    /// </summary>
    public static bool ShouldExitForNegativeRemainingEdge(
        double currentZScore,
        double targetAbsZ,
        double spreadStdDev,
        double referencePrice,
        double roundTripCostFraction)
    {
        double absZ = Math.Abs(currentZScore);
        double tgt = Math.Abs(targetAbsZ);
        if (absZ <= tgt) return false;             // Already at / past target — caller's TP path will fire.
        if (spreadStdDev <= 0) return false;

        double costInSigmas = roundTripCostFraction / spreadStdDev;
        return (absZ - tgt) < costInSigmas;
    }

    /// <summary>
    /// Exit when bars-held exceeds the OU-implied time budget — a multiple of
    /// half-life, calibrated so the expected residual to target is small.
    ///
    /// budgetBars = halfLifeBars × multiplier  (default multiplier 2.0 ≈ 75% reversion).
    /// </summary>
    public static bool ShouldExitForTimeBudget(
        int barsHeld,
        double halfLifeBars,
        double multiplier = 2.0)
    {
        if (halfLifeBars <= 0 || double.IsInfinity(halfLifeBars) || double.IsNaN(halfLifeBars))
            return false;
        double budget = halfLifeBars * multiplier;
        return barsHeld >= budget;
    }

    /// <summary>
    /// Combined OU exit decision. Returns the first reason that fires.
    /// Order: stop-loss (caller checks |z| ≥ stop) → TP (caller checks |z| ≤ target)
    ///        → negative-remaining-edge → time-budget → hold.
    /// This method handles only the latter two — TP/SL are already in PairsBacktestRunner
    /// and run before this is called.
    /// </summary>
    public sealed record Decision(bool ShouldExit, string Reason);

    public static Decision Evaluate(
        int barsHeld,
        double currentZScore,
        double targetAbsZ,
        double halfLifeBars,
        double spreadStdDev,
        double referencePrice,
        double roundTripCostFraction,
        double timeBudgetMultiplier = 2.0)
    {
        if (ShouldExitForNegativeRemainingEdge(
                currentZScore, targetAbsZ, spreadStdDev, referencePrice, roundTripCostFraction))
            return new Decision(true, "OU:NegativeRemainingEdge");

        if (ShouldExitForTimeBudget(barsHeld, halfLifeBars, timeBudgetMultiplier))
            return new Decision(true, "OU:TimeBudgetExceeded");

        return new Decision(false, "OU:Hold");
    }
}
