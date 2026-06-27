namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Pure decision logic for the alt-short AGGREGATE exposure cap.
///
/// [2026-05-21 Quantlab universe-expansion handoff §3] Quantlab proved that the
/// alt-shorts (<c>op_breakout_4h_short</c>, <c>fil_breakout_4h_short</c>, the
/// xs-neutral basket's SHORT leg, …) are NOT independent edges — they are ONE
/// correlated factor ("alts fall together → all shorts lose at once"). Trade-level
/// Sharpe gave a diversification ILLUSION; a naive 11-alt-short basket backtested to
/// <b>-82% portfolio maxDD</b>. So their COMBINED gross-short notional must be capped
/// to roughly a single edge's worth.
///
/// This mirrors <see cref="EdgeLabPerSymbolCapEvaluator"/> but aggregates across the
/// whole <c>exposure_group == "alt_short"</c> set rather than one symbol:
///   - Cap is a fraction of basis ([0,1]) = "1 unit" ≈ one full single-asset short.
///   - Skip-only when breached (no scale-down — keeps kill-switch / promotion-gate
///     interpretation clean, matching the per-symbol cap's frozen policy).
///   - Enforced in Live mode only; DryRun yields an informational decision.
///   - Only constrains orders that INCREASE short exposure. LONG legs and
///     short-REDUCING (closing) orders are never blocked.
///   - "Other alt-short" exposure = aggregate group short notional minus this
///     strategy's own current short piece (treats the rebalance as
///     "cancel my piece, place new piece" so peers' contributions are preserved).
/// </summary>
public static class EdgeLabAltShortCapEvaluator
{
    /// <param name="capFraction">Aggregate alt-short cap as a fraction of basis (0..1).
    ///   Null = no cap configured → <see cref="EdgeLabAltShortCapAction.NoCapConfigured"/>.</param>
    /// <param name="basis">PaperPortfolio.InitialBalance in USD.</param>
    /// <param name="targetShortNotionalUsd">Absolute short notional this strategy wants
    ///   the position to reach after the rebalance (>= 0; pass 0 for non-short targets).</param>
    /// <param name="thisStrategyCurrentShortNotionalUsd">Absolute short notional this
    ///   strategy currently holds (>= 0).</param>
    /// <param name="groupTotalShortNotionalUsd">Absolute sum of short notional across ALL
    ///   alt-short symbols on the live exchange (or synthetic in DryRun); >= 0.</param>
    /// <param name="isLive">True when running in Live mode (cap blocks). False = DryRun (warn-only).</param>
    public static EdgeLabAltShortCapDecision Evaluate(
        decimal? capFraction,
        decimal basis,
        decimal targetShortNotionalUsd,
        decimal thisStrategyCurrentShortNotionalUsd,
        decimal groupTotalShortNotionalUsd,
        bool isLive)
    {
        if (capFraction is not decimal frac)
        {
            return new EdgeLabAltShortCapDecision(
                EdgeLabAltShortCapAction.NoCapConfigured, 0m, 0m, 0m, 0m, "no alt_short cap configured");
        }

        if (frac < 0m || frac > 1m)
        {
            return new EdgeLabAltShortCapDecision(
                EdgeLabAltShortCapAction.InvalidCap, 0m, 0m, 0m, 0m, $"alt_short cap fraction {frac} out of [0,1]");
        }

        var capUsd = basis * frac;

        var thisAbs = System.Math.Abs(thisStrategyCurrentShortNotionalUsd);
        var groupAbs = System.Math.Abs(groupTotalShortNotionalUsd);
        var otherAbs = System.Math.Max(0m, groupAbs - thisAbs);
        var targetAbs = System.Math.Abs(targetShortNotionalUsd);

        var wouldBeTotalAbs = otherAbs + targetAbs;
        var headroom = capUsd - otherAbs;

        // Never block a rebalance that does NOT increase the group's total short.
        // (Reducing/closing this strategy's short, or holding, is always allowed.)
        if (wouldBeTotalAbs <= capUsd || targetAbs <= thisAbs)
        {
            return new EdgeLabAltShortCapDecision(
                EdgeLabAltShortCapAction.AllowedUnderCap, capUsd, groupAbs, wouldBeTotalAbs, headroom,
                $"would_be ${wouldBeTotalAbs:F2} <= cap ${capUsd:F2}");
        }

        if (isLive)
        {
            return new EdgeLabAltShortCapDecision(
                EdgeLabAltShortCapAction.SkipBreach, capUsd, groupAbs, wouldBeTotalAbs, headroom,
                $"alt_short would_be ${wouldBeTotalAbs:F2} > cap ${capUsd:F2} (other=${otherAbs:F2}, target=${targetAbs:F2})");
        }

        return new EdgeLabAltShortCapDecision(
            EdgeLabAltShortCapAction.WouldBreachInDryRun, capUsd, groupAbs, wouldBeTotalAbs, headroom,
            $"DryRun alt_short would-breach: would_be ${wouldBeTotalAbs:F2} > cap ${capUsd:F2}");
    }
}

public enum EdgeLabAltShortCapAction
{
    NoCapConfigured,
    AllowedUnderCap,
    SkipBreach,
    WouldBreachInDryRun,
    InvalidCap,
}

public readonly record struct EdgeLabAltShortCapDecision(
    EdgeLabAltShortCapAction Action,
    decimal CapUsd,
    decimal CurrentGroupAbsUsd,
    decimal WouldBeGroupAbsUsd,
    decimal HeadroomUsd,
    string Reason)
{
    public bool ShouldBlockSubmit => Action == EdgeLabAltShortCapAction.SkipBreach;
}
