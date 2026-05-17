namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Pure decision logic for per-symbol notional cap enforcement on EdgeLab
/// single-asset strategies. Decoupled from DI / hosted-service plumbing so the
/// policy is unit-testable in isolation.
///
/// Sprint 7 (2026-05-11) — defends against the multi-strategy-on-single-asset
/// concentration that the existing 4 BTC strategies already exhibit
/// (fee_panic + stablecoin_30d + rv_breakout + btc_breakout_4h_long sum to
/// weight 0.31 → $930 at $2K basis × 1.5 leverage on co-fire).
///
/// Policy (frozen):
///   - Cap is expressed as a fraction of basis ([0, 1]).
///   - Cap is enforced in Live mode only. DryRun produces an informational
///     decision (<see cref="EdgeLabPerSymbolCapDecision.WouldBreachInDryRun"/>)
///     so the operator sees the warning before live promotion.
///   - Skip-only when breached — the cycle returns without submitting. Scale-down
///     was considered but complicates kill-switch / promotion-gate interpretation.
///   - "Total" exposure means absolute notional aggregated across all OKX
///     positions on the symbol — including this strategy's current position.
///     The would-be total is computed as: (totalCurrent - |thisStrategyCurrent|)
///     + |target|. This treats the rebalance as "cancel my piece, place new
///     piece" so other strategies' contributions are preserved.
/// </summary>
public static class EdgeLabPerSymbolCapEvaluator
{
    /// <summary>
    /// Evaluates whether a proposed rebalance would breach the configured
    /// per-symbol cap. Returns a decision record describing the outcome and
    /// the numeric values that drove it (for logging / telemetry).
    /// </summary>
    /// <param name="symbol">Symbol the rebalance targets (case-insensitive).</param>
    /// <param name="basis">PaperPortfolio.InitialBalance in USD.</param>
    /// <param name="targetNotionalUsd">Signed notional this strategy wants the
    ///   symbol position to reach after the rebalance.</param>
    /// <param name="thisStrategyCurrentNotionalUsd">Signed notional this strategy
    ///   currently holds on the symbol (from the strategy's own synthetic or
    ///   exchange-attributed accounting).</param>
    /// <param name="totalSymbolNotionalUsd">Signed sum of all open positions on
    ///   the symbol on the live exchange (or synthetic in DryRun).</param>
    /// <param name="capFraction">Configured cap as fraction of basis (0..1).
    ///   Null = no cap configured for this symbol → <see cref="EdgeLabPerSymbolCapAction.NoCapConfigured"/>.</param>
    /// <param name="isLive">True when the strategy is running in Live mode.
    ///   False (DryRun) produces an informational decision rather than blocking.</param>
    public static EdgeLabPerSymbolCapDecision Evaluate(
        string symbol,
        decimal basis,
        decimal targetNotionalUsd,
        decimal thisStrategyCurrentNotionalUsd,
        decimal totalSymbolNotionalUsd,
        decimal? capFraction,
        bool isLive)
    {
        if (capFraction is not decimal frac)
        {
            return new EdgeLabPerSymbolCapDecision(
                Action: EdgeLabPerSymbolCapAction.NoCapConfigured,
                Symbol: symbol,
                CapUsd: 0m,
                CurrentTotalAbsUsd: 0m,
                WouldBeTotalAbsUsd: 0m,
                HeadroomUsd: 0m,
                Reason: "no cap configured");
        }

        if (frac < 0m || frac > 1m)
        {
            return new EdgeLabPerSymbolCapDecision(
                Action: EdgeLabPerSymbolCapAction.InvalidCap,
                Symbol: symbol,
                CapUsd: 0m,
                CurrentTotalAbsUsd: 0m,
                WouldBeTotalAbsUsd: 0m,
                HeadroomUsd: 0m,
                Reason: $"cap fraction {frac} out of [0,1]");
        }

        var capUsd = basis * frac;

        // Other strategies' contribution = current total minus this strategy's piece.
        // Take absolute values so opposite-sign positions don't artificially "net".
        // Crypto perps allow same-symbol hedged positions only in HedgeMode (OKX);
        // even in NetMode opposite-side strategies represent real notional usage.
        var thisAbs = System.Math.Abs(thisStrategyCurrentNotionalUsd);
        var totalAbs = System.Math.Abs(totalSymbolNotionalUsd);
        var otherAbs = System.Math.Max(0m, totalAbs - thisAbs);
        var targetAbs = System.Math.Abs(targetNotionalUsd);

        var wouldBeTotalAbs = otherAbs + targetAbs;
        var headroom = capUsd - otherAbs;

        if (wouldBeTotalAbs <= capUsd)
        {
            return new EdgeLabPerSymbolCapDecision(
                Action: EdgeLabPerSymbolCapAction.AllowedUnderCap,
                Symbol: symbol,
                CapUsd: capUsd,
                CurrentTotalAbsUsd: totalAbs,
                WouldBeTotalAbsUsd: wouldBeTotalAbs,
                HeadroomUsd: headroom,
                Reason: $"would_be ${wouldBeTotalAbs:F2} ≤ cap ${capUsd:F2}");
        }

        // Cap would be breached.
        if (isLive)
        {
            return new EdgeLabPerSymbolCapDecision(
                Action: EdgeLabPerSymbolCapAction.SkipBreach,
                Symbol: symbol,
                CapUsd: capUsd,
                CurrentTotalAbsUsd: totalAbs,
                WouldBeTotalAbsUsd: wouldBeTotalAbs,
                HeadroomUsd: headroom,
                Reason: $"would_be ${wouldBeTotalAbs:F2} > cap ${capUsd:F2} (other_abs=${otherAbs:F2}, target_abs=${targetAbs:F2})");
        }

        // DryRun: log-only warning, proceed.
        return new EdgeLabPerSymbolCapDecision(
            Action: EdgeLabPerSymbolCapAction.WouldBreachInDryRun,
            Symbol: symbol,
            CapUsd: capUsd,
            CurrentTotalAbsUsd: totalAbs,
            WouldBeTotalAbsUsd: wouldBeTotalAbs,
            HeadroomUsd: headroom,
            Reason: $"DryRun would-breach: would_be ${wouldBeTotalAbs:F2} > cap ${capUsd:F2}");
    }
}

public enum EdgeLabPerSymbolCapAction
{
    /// <summary>No PerSymbolCap entry for this symbol — rebalance proceeds.</summary>
    NoCapConfigured,

    /// <summary>Cap configured and rebalance is within budget — proceed.</summary>
    AllowedUnderCap,

    /// <summary>Cap would be breached in Live mode → skip the order submission.</summary>
    SkipBreach,

    /// <summary>Cap would be breached in DryRun → log warning but proceed.</summary>
    WouldBreachInDryRun,

    /// <summary>Cap fraction is out of [0, 1] → treat as misconfiguration; log + proceed.</summary>
    InvalidCap,
}

public readonly record struct EdgeLabPerSymbolCapDecision(
    EdgeLabPerSymbolCapAction Action,
    string Symbol,
    decimal CapUsd,
    decimal CurrentTotalAbsUsd,
    decimal WouldBeTotalAbsUsd,
    decimal HeadroomUsd,
    string Reason)
{
    public bool ShouldBlockSubmit => Action == EdgeLabPerSymbolCapAction.SkipBreach;
}
