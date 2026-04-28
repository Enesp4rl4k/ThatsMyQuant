using System;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Pure-function policy module that decides HOW to place an order so the
/// system collects OKX's maker rebate (currently -0.02% on perps for VIP1+),
/// while still guaranteeing a fill within a bounded latency budget.
///
/// Why this matters in the upgrade plan:
///   Phase G1 — round-trip fee budget on a typical pair trade is ~10 bps with
///   default taker pricing. If we capture maker rebate on at least one leg,
///   that drops to ~6 bps; both legs maker → 2 bps + the rebate. Across 200+
///   trades / month this is the single biggest cost-side improvement in the
///   plan. No new alpha generation — it's the equivalent of recovering
///   negative slippage we are paying today.
///
/// Decision logic:
///   1. Compute a maker offset from mid: side-aware,  proportional to half the
///      top-of-book spread (so we sit at top of book, the winning maker price).
///   2. Pick a timeout that bounds the wait. If unfilled at timeout, fall back
///      to a market order (or, optionally, to a tighter aggressive-limit retry).
///
/// Inputs are exchange-snapshot agnostic: caller passes the current mid, the
/// top-of-book spread, the desired side, and an "urgency" 0-1 dial that
/// determines the offset and timeout. urgency = 0 → patient maker (tight
/// offset, long timeout); urgency = 1 → near-cross (small offset, short
/// timeout, falls back to market quickly).
/// </summary>
public static class MakerExecutionPolicy
{
    /// <summary>
    /// Concrete order plan from the policy.
    /// </summary>
    /// <param name="OrderType">Recommended order type (Limit when maker eligible; Market when urgent or no quote).</param>
    /// <param name="LimitPrice">Limit price for the order (only meaningful when OrderType is Limit).</param>
    /// <param name="TimeoutMs">Timeout after which an unfilled limit should be cancelled and the
    /// fallback executed.</param>
    /// <param name="FallbackOnTimeout">What to do if the limit times out unfilled.</param>
    /// <param name="ExpectedMakerRebateBps">Expected rebate captured if the order fills as maker (informational).</param>
    /// <param name="Reason">Human-readable explanation of the choice for telemetry.</param>
    public sealed record OrderPlan(
        OrderType OrderType,
        decimal LimitPrice,
        int TimeoutMs,
        FallbackAction FallbackOnTimeout,
        double ExpectedMakerRebateBps,
        string Reason);

    public enum FallbackAction { Market, TighterLimit, Cancel }

    /// <summary>OKX VIP1 maker rebate at the time of writing (negative = paid TO us).</summary>
    public const double DefaultMakerRebateBps = -2.0;
    /// <summary>OKX VIP1 taker fee for fallback comparison.</summary>
    public const double DefaultTakerFeeBps = 5.0;

    /// <summary>
    /// Compute the order plan for a desired entry / exit.
    /// </summary>
    /// <param name="side">Buy or Sell.</param>
    /// <param name="mid">Current mid price.</param>
    /// <param name="topOfBookSpread">Best-ask − best-bid; used to size the maker offset.</param>
    /// <param name="urgency">∈ [0,1]. 0 = patient (sit one tick inside, long timeout), 1 = near-cross.</param>
    /// <param name="patientTimeoutMs">Timeout when urgency = 0. Default 30s.</param>
    /// <param name="urgentTimeoutMs">Timeout when urgency = 1. Default 3s.</param>
    /// <param name="fallback">What to do if limit times out unfilled.</param>
    public static OrderPlan Plan(
        TradeSide side,
        decimal mid,
        decimal topOfBookSpread,
        double urgency,
        int patientTimeoutMs = 30_000,
        int urgentTimeoutMs = 3_000,
        FallbackAction fallback = FallbackAction.Market)
    {
        if (mid <= 0)
            throw new ArgumentOutOfRangeException(nameof(mid), "Mid must be positive.");
        if (urgency < 0 || urgency > 1)
            throw new ArgumentOutOfRangeException(nameof(urgency), "Urgency must be in [0, 1].");
        if (topOfBookSpread < 0)
            throw new ArgumentOutOfRangeException(nameof(topOfBookSpread), "Spread must be non-negative.");
        if (patientTimeoutMs <= 0 || urgentTimeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(patientTimeoutMs), "Timeouts must be positive.");

        // Degenerate: zero spread → can't place inside, just go market.
        if (topOfBookSpread == 0m)
        {
            return new OrderPlan(
                OrderType: OrderType.Market,
                LimitPrice: mid,
                TimeoutMs: urgentTimeoutMs,
                FallbackOnTimeout: FallbackAction.Cancel,
                ExpectedMakerRebateBps: 0,
                Reason: "Zero spread — cannot place maker, going market.");
        }

        // Maker offset: at urgency 0 sit one full half-spread inside (true maker, top of
        // book). At urgency 1 sit one tick inside (very aggressive maker — likely to fill
        // but still earns rebate when it does). Interpolate linearly.
        decimal halfSpread = topOfBookSpread / 2m;
        // Use a small minimum tick proxy: 1/10 of spread or 0.0001 of mid, whichever is smaller.
        decimal minTick = Math.Min(topOfBookSpread / 10m, mid * 0.0001m);
        if (minTick <= 0) minTick = topOfBookSpread / 10m;

        // patient (u=0): offset = halfSpread (sit at the inside of bid/ask)
        // urgent  (u=1): offset = minTick   (sit barely inside best price)
        decimal offset = halfSpread - (decimal)urgency * (halfSpread - minTick);
        if (offset < minTick) offset = minTick;

        decimal limitPrice = side == TradeSide.Buy ? mid - offset : mid + offset;

        int timeout = (int)Math.Round(
            patientTimeoutMs - urgency * (patientTimeoutMs - urgentTimeoutMs));

        return new OrderPlan(
            OrderType: OrderType.Limit,
            LimitPrice: limitPrice,
            TimeoutMs: timeout,
            FallbackOnTimeout: fallback,
            ExpectedMakerRebateBps: DefaultMakerRebateBps,
            Reason: $"Maker post-only at {(urgency * 100):F0}% urgency, offset={offset:F6}");
    }

    /// <summary>
    /// Round-trip cost saving estimate (in bps) for a fully-maker round trip vs
    /// the all-taker baseline. Negative = savings (we pay less). Useful for
    /// pre-trade EV calculations: only enter a marginal trade if expected edge
    /// covers cost AFTER applying this saving.
    /// </summary>
    public static double EstimateRoundTripSavingsBps(
        double makerFillProbability,
        double makerRebateBps = DefaultMakerRebateBps,
        double takerFeeBps = DefaultTakerFeeBps)
    {
        if (makerFillProbability < 0 || makerFillProbability > 1)
            throw new ArgumentOutOfRangeException(nameof(makerFillProbability), "Must be in [0, 1].");

        // Per leg expected cost: P(maker fill) × makerRebate + (1 − P) × takerFee.
        // Round-trip = 2 × per-leg.
        double perLegExpected = makerFillProbability * makerRebateBps + (1 - makerFillProbability) * takerFeeBps;
        double allTakerRoundTrip = 2 * takerFeeBps;
        double mixedRoundTrip = 2 * perLegExpected;
        return mixedRoundTrip - allTakerRoundTrip;  // Negative = savings.
    }
}
