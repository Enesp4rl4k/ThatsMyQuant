using System;
using System.Collections.Generic;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Cross-exchange (multi-venue) arbitrage detector. The same underlying perp
/// or spot trades on multiple venues simultaneously (OKX, Binance, Bybit, etc.).
/// Latency, fee asymmetry, and order-book depth differences create transient
/// price divergences that revert within seconds-to-minutes. Each event is a
/// near-arbitrage if both legs can be filled, accounting for taker fees and
/// funding-rate differential on perps.
///
/// Why this matters in the upgrade plan:
///   Phase Tier-1C — uses the existing ArbitrageAgent infrastructure, just
///   needs the algorithmic core. Recurring small alpha (5-15bp/event, 10-30
///   events/day) with very low directional risk because both legs hedge each
///   other once filled. The challenge is execution speed, not alpha discovery.
///
/// References:
///   Makarov &amp; Schoar (2020), "Trading and Arbitrage in Cryptocurrency
///     Markets", Journal of Financial Economics 135(2): 293-319 — establishes
///     persistent cross-exchange arbitrage in BTC at the 1-10 minute horizon.
///   Borri &amp; Shakhnov (2018), "Cryptomarket Discounts" — bid-ask spread
///     dynamics across venues.
///
/// Algorithm:
///   1. For a pair of venues, compute spread = (priceA − priceB) / mid.
///   2. Trigger when |spread| ≥ entryThreshold (after fees on both legs).
///   3. The arbitrage trade: BUY the cheaper venue, SELL the expensive one.
///   4. Exit: spread converges back to within exitThreshold (typically near 0).
///
/// Caller responsibility: connect to Binance / Bybit market-data adapters.
/// This module is pure decision logic over already-fetched mid prices.
/// </summary>
public static class CrossExchangeArbitrage
{
    /// <summary>One arbitrage opportunity evaluation.</summary>
    /// <param name="Symbol">Underlying asset (e.g., "BTC-USDT-SWAP").</param>
    /// <param name="VenueA">First venue name.</param>
    /// <param name="VenueB">Second venue name.</param>
    /// <param name="PriceA">Mid price on venue A.</param>
    /// <param name="PriceB">Mid price on venue B.</param>
    /// <param name="SpreadBps">(PriceA − PriceB) / Mid expressed in basis points.</param>
    /// <param name="ExpectedEdgeBps">Spread minus expected total fees / funding differential.</param>
    /// <param name="Trigger">True when expected edge is above threshold.</param>
    /// <param name="BuyVenue">Venue to BUY on (the cheaper side).</param>
    /// <param name="SellVenue">Venue to SELL on (the expensive side).</param>
    public sealed record ArbEvaluation(
        string Symbol,
        string VenueA,
        string VenueB,
        decimal PriceA,
        decimal PriceB,
        double SpreadBps,
        double ExpectedEdgeBps,
        bool Trigger,
        string? BuyVenue,
        string? SellVenue);

    /// <summary>Default minimum edge in bps (after fees) to trigger an entry.</summary>
    public const double DefaultEntryEdgeBps = 5.0;
    /// <summary>Default round-trip taker fee in bps for one venue.</summary>
    public const double DefaultRoundTripFeeBps = 6.0;

    /// <summary>
    /// Evaluate one venue-pair tick.
    /// </summary>
    /// <param name="symbol">Underlying asset / contract identifier.</param>
    /// <param name="venueA">Name of first venue.</param>
    /// <param name="venueB">Name of second venue.</param>
    /// <param name="priceA">Mid on venue A.</param>
    /// <param name="priceB">Mid on venue B.</param>
    /// <param name="venueAFeeRoundTripBps">Round-trip taker fee for venue A in bps.</param>
    /// <param name="venueBFeeRoundTripBps">Round-trip taker fee for venue B in bps.</param>
    /// <param name="fundingDifferentialBps">For perps: expected funding paid/received over hold (bps). Default 0.</param>
    /// <param name="entryEdgeThresholdBps">Minimum (post-fee) edge in bps to fire. Default 5.</param>
    public static ArbEvaluation Evaluate(
        string symbol,
        string venueA,
        string venueB,
        decimal priceA,
        decimal priceB,
        double venueAFeeRoundTripBps = DefaultRoundTripFeeBps,
        double venueBFeeRoundTripBps = DefaultRoundTripFeeBps,
        double fundingDifferentialBps = 0.0,
        double entryEdgeThresholdBps = DefaultEntryEdgeBps)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol required.", nameof(symbol));
        if (string.IsNullOrWhiteSpace(venueA)) throw new ArgumentException("venueA required.", nameof(venueA));
        if (string.IsNullOrWhiteSpace(venueB)) throw new ArgumentException("venueB required.", nameof(venueB));
        if (priceA <= 0 || priceB <= 0)
            throw new ArgumentOutOfRangeException(nameof(priceA), "Prices must be positive.");

        decimal mid = (priceA + priceB) / 2m;
        double spreadBps = (double)((priceA - priceB) / mid) * 10_000.0;

        // Total cost = both venues' round-trip fees + funding differential
        // (each leg is one half of a round trip — but caller's "round trip" already includes both
        //  open and close on a single venue, so total cost = sum of both venue round-trip fees / 2,
        //  i.e., one open + one close across the two venues).
        double totalCostBps = (venueAFeeRoundTripBps + venueBFeeRoundTripBps) / 2.0 + Math.Abs(fundingDifferentialBps);
        double absSpread = Math.Abs(spreadBps);
        double expectedEdge = absSpread - totalCostBps;
        bool trigger = expectedEdge >= entryEdgeThresholdBps;

        string? buy = null, sell = null;
        if (trigger)
        {
            if (spreadBps > 0) { sell = venueA; buy = venueB; }   // A is expensive
            else                { sell = venueB; buy = venueA; }
        }

        return new ArbEvaluation(
            Symbol: symbol,
            VenueA: venueA,
            VenueB: venueB,
            PriceA: priceA,
            PriceB: priceB,
            SpreadBps: spreadBps,
            ExpectedEdgeBps: expectedEdge,
            Trigger: trigger,
            BuyVenue: buy,
            SellVenue: sell);
    }

    /// <summary>
    /// Compute exit recommendation: spread has converged enough (in bps) that
    /// expected remaining edge no longer covers cost — close both legs.
    /// </summary>
    public static bool ShouldExit(
        double currentSpreadBps,
        double entrySpreadBps,
        double exitFraction = 0.5)
    {
        // Exit when current spread shrinks to entry × (1 − fraction).
        if (Math.Sign(currentSpreadBps) != Math.Sign(entrySpreadBps)) return true;  // sign-flip → exit
        double targetMagnitude = Math.Abs(entrySpreadBps) * (1.0 - exitFraction);
        return Math.Abs(currentSpreadBps) <= targetMagnitude;
    }
}
