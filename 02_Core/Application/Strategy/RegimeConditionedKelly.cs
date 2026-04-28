using System;
using System.Collections.Generic;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Kelly fraction computation conditional on the current market regime, instead
/// of pooling all historical trades. Directly addresses the well-known failure
/// mode where a strategy with strong overall WR loses badly in one specific
/// regime — averaging across regimes inflates the global Kelly fraction and the
/// system over-bets exactly when the edge has temporarily vanished.
///
/// Reference:
///   Kelly (1956), "A New Interpretation of Information Rate".
///   Thorp (2006), "The Kelly Criterion in Blackjack, Sports Betting, and the
///     Stock Market", Handbook of Asset and Liability Management.
///   MacLean, Thorp, Ziemba (2010), "The Kelly Capital Growth Investment Criterion".
///
/// Standard Kelly:        f* = (p·b − q) / b,    where p=WR, q=1−p, b=avgWin/|avgLoss|
/// Regime Kelly:          f*_regime = same formula but using p_regime, b_regime
/// Fractional Kelly:      f' = f* · ϑ, ϑ ∈ [0.25, 0.5] — reduces variance / drawdown
///                              at the cost of long-run growth rate.
///
/// Why this matters for the upgrade plan:
///   Phase B2 — pair and funding strategies have different historical performance
///   per regime. Pairs trade win-rate is e.g. ~58% in Range, ~42% in Trending.
///   Using a 50% pooled WR for Kelly oversizes positions during Trending periods,
///   contributing to drawdowns. Regime-Kelly cuts size when the regime is wrong
///   for the strategy, restoring it when conditions are favorable.
///
/// Caller responsibility: maintain per-regime trade history (wins, losses, mean
/// win, mean loss). This module computes the fraction; it does not collect data.
/// </summary>
public static class RegimeConditionedKelly
{
    /// <summary>
    /// Per-regime trade statistics aggregate.
    /// </summary>
    /// <param name="Regime">The regime label.</param>
    /// <param name="Wins">Number of winning trades in this regime.</param>
    /// <param name="Losses">Number of losing trades in this regime (positive count, even though P&amp;L was negative).</param>
    /// <param name="AverageWin">Mean P&amp;L of winning trades (positive).</param>
    /// <param name="AverageLoss">Mean absolute P&amp;L of losing trades (positive — caller passes the magnitude).</param>
    public sealed record RegimeStats(
        MarketRegime Regime,
        int Wins,
        int Losses,
        double AverageWin,
        double AverageLoss)
    {
        public int Total => Wins + Losses;
        public double WinRate => Total > 0 ? (double)Wins / Total : 0.0;
        public double WinLossRatio => AverageLoss > 0 ? AverageWin / AverageLoss : 0.0;
    }

    /// <summary>
    /// Result of a Kelly computation.
    /// </summary>
    /// <param name="Fraction">Position size as fraction of capital, ∈ [0, MaxFraction]. 0 means do not trade.</param>
    /// <param name="UsedRegime">The regime whose stats were used (current regime if it had enough samples; fallback otherwise).</param>
    /// <param name="UsedFallback">True if the current regime had insufficient samples and we fell back to pooled stats.</param>
    /// <param name="RawFullKelly">Unfractioned Kelly value before the conservatism multiplier was applied.</param>
    /// <param name="WinRateUsed">The p̂ used in the calculation.</param>
    /// <param name="WinLossRatioUsed">The b̂ used in the calculation.</param>
    public sealed record KellyResult(
        double Fraction,
        MarketRegime UsedRegime,
        bool UsedFallback,
        double RawFullKelly,
        double WinRateUsed,
        double WinLossRatioUsed);

    /// <summary>
    /// Compute the regime-conditioned Kelly fraction.
    /// </summary>
    /// <param name="currentRegime">Current market regime (used to pick the relevant stats row).</param>
    /// <param name="history">Per-regime stats. Multiple rows allowed; latest by Total wins picked when duplicates.</param>
    /// <param name="fractionalKelly">Conservatism multiplier ∈ (0, 1]. 0.25 (quarter-Kelly) is the
    /// industry default for crypto strategies — full Kelly's drawdown variance is too aggressive
    /// for small-account capital preservation. Default 0.25.</param>
    /// <param name="minSamplesForRegime">Minimum trades required in the current regime before its
    /// stats are trusted; otherwise fall back to pooled stats. Default 20.</param>
    /// <param name="maxFraction">Hard cap on the returned fraction. Default 0.25 (25% of capital).</param>
    public static KellyResult Compute(
        MarketRegime currentRegime,
        IEnumerable<RegimeStats> history,
        double fractionalKelly = 0.25,
        int minSamplesForRegime = 20,
        double maxFraction = 0.25)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (fractionalKelly <= 0 || fractionalKelly > 1)
            throw new ArgumentOutOfRangeException(nameof(fractionalKelly), "Must be in (0, 1].");
        if (minSamplesForRegime < 1)
            throw new ArgumentOutOfRangeException(nameof(minSamplesForRegime), "Must be ≥ 1.");
        if (maxFraction <= 0 || maxFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(maxFraction), "Must be in (0, 1].");

        // Materialize once; we may iterate multiple times.
        RegimeStats? regimeRow = null;
        int totalWins = 0;
        int totalLosses = 0;
        double totalWinSum = 0.0;
        double totalLossSum = 0.0;
        foreach (var row in history)
        {
            totalWins += row.Wins;
            totalLosses += row.Losses;
            totalWinSum += row.AverageWin * row.Wins;
            totalLossSum += row.AverageLoss * row.Losses;
            if (row.Regime == currentRegime &&
                (regimeRow == null || row.Total > regimeRow.Total))
            {
                regimeRow = row;
            }
        }

        bool usedFallback = false;
        MarketRegime usedRegime = currentRegime;
        double p, b;
        if (regimeRow != null && regimeRow.Total >= minSamplesForRegime)
        {
            p = regimeRow.WinRate;
            b = regimeRow.WinLossRatio;
        }
        else
        {
            // Fallback: pooled stats (or zeros if no history at all).
            usedFallback = true;
            usedRegime = MarketRegime.Unknown;
            int pooledTotal = totalWins + totalLosses;
            if (pooledTotal == 0)
                return new KellyResult(0.0, MarketRegime.Unknown, true, 0.0, 0.0, 0.0);
            p = (double)totalWins / pooledTotal;
            double avgWin = totalWins > 0 ? totalWinSum / totalWins : 0.0;
            double avgLoss = totalLosses > 0 ? totalLossSum / totalLosses : 0.0;
            b = avgLoss > 0 ? avgWin / avgLoss : 0.0;
        }

        // Kelly: f = (p·b − q) / b
        double q = 1.0 - p;
        double rawKelly = b > 0 ? (p * b - q) / b : 0.0;

        // Negative-EV guard: never enter when Kelly is non-positive.
        if (rawKelly <= 0)
            return new KellyResult(0.0, usedRegime, usedFallback, rawKelly, p, b);

        double fractioned = rawKelly * fractionalKelly;
        double clamped = Math.Min(fractioned, maxFraction);
        return new KellyResult(clamped, usedRegime, usedFallback, rawKelly, p, b);
    }
}
