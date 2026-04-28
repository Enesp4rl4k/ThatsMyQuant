using System;
using System.Collections.Generic;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// BTC dominance regime overlay. Computes a sizing multiplier for altcoin
/// positions (and a complementary BTC-overweight signal) based on the momentum
/// of BTC dominance — the BTC market-cap share of total crypto market cap.
///
/// Empirical regularity:
///   • BTC.D rising → capital rotating INTO Bitcoin out of altcoins → alts
///     systematically underperform BTC, often even in absolute terms.
///   • BTC.D falling → altseason setup → alts outperform.
///   • Regime persistence: BTC.D trends for 30-90 day stretches; daily noise is
///     mostly mean-reverting around the trend.
///
/// References:
///   Liu &amp; Tsyvinski (2021), "Risks and Returns of Cryptocurrency", Review of
///     Financial Studies — section on cross-section of crypto returns.
///   Internal scan, 2026-04-24 strategy_research_archive — pairs and funding
///     edges measured against BTC.D regime show ±25-40bp/day return spread
///     between BTC.D-rising and BTC.D-falling weeks.
///
/// Why this matters in the upgrade plan:
///   Phase E1 — adds a portfolio-level overlay that cuts alt exposure when the
///   structural backdrop is hostile and increases it when conditions are
///   favorable. Combined with Phase B1 (HRP) and B2 (Regime-Kelly) the system
///   becomes regime-aware on three axes: market microstructure, strategy WR
///   per regime, and macro crypto rotation.
///
/// Algorithm (default 30-day momentum):
///   momentum = (BTC.D_today − BTC.D_30daysAgo) / BTC.D_30daysAgo
///   altMultiplier = clamp(1 − α · momentum, multiplierFloor, multiplierCeiling)
///   btcMultiplier = clamp(1 + α · momentum, multiplierFloor, multiplierCeiling)
///
/// At α = 5 (default), a 4% BTC.D spike (e.g., 50% → 52%) reduces alt sizing by
/// ~20% and increases BTC sizing by 20%.  α can be tuned to taste.
/// </summary>
public static class BtcDominanceOverlay
{
    /// <summary>
    /// Result bundle from a dominance-overlay computation.
    /// </summary>
    /// <param name="DominanceMomentum">(D_now − D_lookback) / D_lookback. Range typically [-0.10, +0.10] over 30d.</param>
    /// <param name="AltMultiplier">Multiplier to apply to alt-asset position sizes; ∈ [floor, ceiling].</param>
    /// <param name="BtcMultiplier">Multiplier for BTC-direct exposure; the complement of alt.</param>
    /// <param name="DominanceNow">Current BTC.D fraction (e.g., 0.52).</param>
    /// <param name="DominanceLookback">Lookback BTC.D fraction.</param>
    public sealed record DominanceOverlayResult(
        double DominanceMomentum,
        double AltMultiplier,
        double BtcMultiplier,
        double DominanceNow,
        double DominanceLookback);

    /// <summary>
    /// Compute the overlay multipliers from a BTC.D time series (sorted oldest → newest).
    /// </summary>
    /// <param name="dominanceHistory">Daily BTC.D observations as fractions (0.50 = 50%). Must contain
    /// at least <paramref name="lookbackDays"/> + 1 entries.</param>
    /// <param name="lookbackDays">Days back for the momentum window. Default 30.</param>
    /// <param name="alpha">Sensitivity coefficient. Higher = more aggressive sizing response. Default 5.0.</param>
    /// <param name="multiplierFloor">Lower clamp for the resulting multiplier. Default 0.4 (60% reduction max).</param>
    /// <param name="multiplierCeiling">Upper clamp. Default 1.6 (60% increase max).</param>
    public static DominanceOverlayResult Compute(
        IReadOnlyList<double> dominanceHistory,
        int lookbackDays = 30,
        double alpha = 5.0,
        double multiplierFloor = 0.4,
        double multiplierCeiling = 1.6)
    {
        ArgumentNullException.ThrowIfNull(dominanceHistory);
        if (lookbackDays < 1)
            throw new ArgumentOutOfRangeException(nameof(lookbackDays));
        if (dominanceHistory.Count < lookbackDays + 1)
            throw new ArgumentException($"Need at least {lookbackDays + 1} observations, got {dominanceHistory.Count}.", nameof(dominanceHistory));
        if (multiplierFloor <= 0 || multiplierCeiling <= multiplierFloor)
            throw new ArgumentException("Require 0 < multiplierFloor < multiplierCeiling.");
        if (alpha < 0)
            throw new ArgumentOutOfRangeException(nameof(alpha), "Alpha must be non-negative.");

        int last = dominanceHistory.Count - 1;
        double dNow = dominanceHistory[last];
        double dLookback = dominanceHistory[last - lookbackDays];

        if (dNow <= 0 || dLookback <= 0)
            throw new ArgumentException("BTC.D values must be positive fractions.", nameof(dominanceHistory));

        double momentum = (dNow - dLookback) / dLookback;

        double altRaw = 1.0 - alpha * momentum;
        double btcRaw = 1.0 + alpha * momentum;

        double altClamp = Math.Clamp(altRaw, multiplierFloor, multiplierCeiling);
        double btcClamp = Math.Clamp(btcRaw, multiplierFloor, multiplierCeiling);

        return new DominanceOverlayResult(
            DominanceMomentum: momentum,
            AltMultiplier: altClamp,
            BtcMultiplier: btcClamp,
            DominanceNow: dNow,
            DominanceLookback: dLookback);
    }

    /// <summary>
    /// Apply the overlay's alt multiplier to an asset weight, given whether the
    /// asset is BTC or an alt. Useful when iterating over a portfolio:
    /// <code>
    /// var overlay = BtcDominanceOverlay.Compute(history);
    /// foreach (var (sym, w) in weights)
    ///     newWeight[sym] = w * BtcDominanceOverlay.ScaleWeight(overlay, IsBtc(sym));
    /// </code>
    /// </summary>
    public static double ScaleWeight(DominanceOverlayResult overlay, bool isBtc)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return isBtc ? overlay.BtcMultiplier : overlay.AltMultiplier;
    }
}
