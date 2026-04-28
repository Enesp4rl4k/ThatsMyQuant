using System;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Cross-quote arbitrage: detects price divergences between the SAME underlying
/// asset traded against two different quote currencies (e.g. BTC/USDT vs BTC/USDC).
/// Both contracts settle to the same Bitcoin, so any divergence beyond the
/// stablecoin's own NAV gap is pure arbitrage.
///
/// Why this is the cleanest edge in the upgrade plan:
///   • <b>Zero directional risk</b>: the trade is delta-neutral (long one
///     contract, short the other) once both legs fill.
///   • <b>Bounded by stablecoin de-pegging</b>: most of the time spread is
///     well within ±5bps. The blow-out events (USDC bank crisis 2023,
///     periodic USDT FUD) push the spread to 50-100bps and last a few hours
///     to days — high-edge entries appear in narrow windows.
///   • <b>No new infrastructure</b>: OKX provides BTC-USDT and BTC-USDC perps
///     side by side; the existing market-data adapter already supports both.
///
/// Used in upgrade plan: Phase J3 — easiest of the pair-coverage expansions.
/// Standalone signal generator; the executor wraps two market orders sized
/// equally on the two legs.
///
/// Reference: standard cross-listing arbitrage from equity markets, applied
/// to crypto stablecoins. See also Makarov &amp; Schoar (2020), "Trading and
/// Arbitrage in Cryptocurrency Markets", JFE — Bitcoin price differences
/// across exchanges and quote currencies persist for measurable durations.
/// </summary>
public static class CrossQuoteArbitrage
{
    /// <summary>Result of a single tick's evaluation.</summary>
    /// <param name="Symbol">Underlying asset (e.g., "BTC").</param>
    /// <param name="QuoteA">First quote currency (e.g., "USDT").</param>
    /// <param name="QuoteB">Second quote currency (e.g., "USDC").</param>
    /// <param name="PriceA">Mid price in quote A.</param>
    /// <param name="PriceB">Mid price in quote B.</param>
    /// <param name="SpreadBps">Spread expressed as basis points: (priceA − priceB) / mid.</param>
    /// <param name="Trigger">True when |SpreadBps| ≥ entryThresholdBps (after cost).</param>
    /// <param name="LongLegSide">If trigger: which side to buy (the cheaper quote).</param>
    /// <param name="ShortLegSide">If trigger: which side to sell (the expensive quote).</param>
    public sealed record SpreadEvaluation(
        string Symbol,
        string QuoteA,
        string QuoteB,
        decimal PriceA,
        decimal PriceB,
        double SpreadBps,
        bool Trigger,
        TradeSide? LongLegSide,
        TradeSide? ShortLegSide,
        string? LongQuote,
        string? ShortQuote);

    /// <summary>Default entry threshold in bps. Conservative: assume 5bp round-trip cost on each leg
    /// (10bp per side) → require 15bp spread to leave 5bp of edge. Tune via parameter.</summary>
    public const double DefaultEntryThresholdBps = 15.0;

    /// <summary>
    /// Evaluate the current spread between two quote pricings.
    /// </summary>
    /// <param name="symbol">Underlying asset, e.g., "BTC".</param>
    /// <param name="quoteA">First quote currency name, e.g., "USDT".</param>
    /// <param name="quoteB">Second quote currency name, e.g., "USDC".</param>
    /// <param name="priceA">Current mid price quoted in <paramref name="quoteA"/>.</param>
    /// <param name="priceB">Current mid price quoted in <paramref name="quoteB"/>.</param>
    /// <param name="entryThresholdBps">Minimum spread magnitude (bps) to fire a trigger.</param>
    public static SpreadEvaluation Evaluate(
        string symbol,
        string quoteA,
        string quoteB,
        decimal priceA,
        decimal priceB,
        double entryThresholdBps = DefaultEntryThresholdBps)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol required.", nameof(symbol));
        if (string.IsNullOrWhiteSpace(quoteA)) throw new ArgumentException("quoteA required.", nameof(quoteA));
        if (string.IsNullOrWhiteSpace(quoteB)) throw new ArgumentException("quoteB required.", nameof(quoteB));
        if (priceA <= 0 || priceB <= 0)
            throw new ArgumentOutOfRangeException(nameof(priceA), "Both prices must be positive.");
        if (entryThresholdBps <= 0)
            throw new ArgumentOutOfRangeException(nameof(entryThresholdBps));

        decimal mid = (priceA + priceB) / 2m;
        double spreadBps = mid > 0 ? (double)((priceA - priceB) / mid) * 10_000.0 : 0.0;
        bool trigger = Math.Abs(spreadBps) >= entryThresholdBps;

        TradeSide? longSide = null;
        TradeSide? shortSide = null;
        string? longQuote = null;
        string? shortQuote = null;
        if (trigger)
        {
            // priceA > priceB → quote A is "expensive", quote B is "cheap".
            // Long the cheap, short the expensive — convergence trade.
            if (spreadBps > 0)
            {
                shortQuote = quoteA;
                longQuote = quoteB;
                shortSide = TradeSide.Sell;
                longSide = TradeSide.Buy;
            }
            else
            {
                shortQuote = quoteB;
                longQuote = quoteA;
                shortSide = TradeSide.Sell;
                longSide = TradeSide.Buy;
            }
        }

        return new SpreadEvaluation(
            Symbol: symbol,
            QuoteA: quoteA,
            QuoteB: quoteB,
            PriceA: priceA,
            PriceB: priceB,
            SpreadBps: spreadBps,
            Trigger: trigger,
            LongLegSide: longSide,
            ShortLegSide: shortSide,
            LongQuote: longQuote,
            ShortQuote: shortQuote);
    }

    /// <summary>
    /// Convergence-target z-score: how close is the current spread to its long-run mean,
    /// expressed in spread-stddev units. Useful for the EXIT decision (target = 0σ).
    /// </summary>
    public static double ComputeSpreadZScore(double currentSpreadBps, double rollingMeanBps, double rollingStdBps)
    {
        if (rollingStdBps <= 0) return 0;
        return (currentSpreadBps - rollingMeanBps) / rollingStdBps;
    }
}
