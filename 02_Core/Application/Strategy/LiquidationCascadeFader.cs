using System;
using System.Collections.Generic;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Detects "liquidation cascades" — sudden clusters of forced liquidations on a
/// perpetual swap — and produces a counter-trend (fade) signal. Cascades cause
/// price to mechanically overshoot as long/short positions are force-closed at
/// market; the post-cascade reversion is one of the cleanest event-driven edges
/// in crypto markets.
///
/// Mechanism:
///   When BTC drops 2-3%, leverage longs hit liquidation thresholds and their
///   positions are auto-closed at market. Each closure adds sell pressure,
///   triggering the next set of longs to liquidate — a cascade. Within minutes
///   the local market is "cleared" of weak hands; the price recovers to a fair
///   level over the next 5-30 minutes. The reverse pattern (short cascades) is
///   symmetric.
///
/// References:
///   Coinglass / DefiLlama liquidation feed analysis.
///   Internal scan, 2026-04-24 strategy_research_archive — historic OKX
///     liquidation events show 30-80bp/event mean reversion edge with
///     ~10-15 events/month at the BTC $5M / alt $1M trigger thresholds.
///
/// Why this matters in the upgrade plan:
///   Phase D1 — adds a high-edge, event-driven NEW alpha source. Uses OKX's
///   `liquidation-orders` WebSocket channel (already available). Trades are
///   short-duration (5-30 minute hold), low correlation to the rest of the
///   portfolio (event-triggered, not regime-triggered). Highest single
///   alpha-per-event in the entire upgrade plan.
///
/// Algorithm:
///   1. Aggregate liquidation notional in a rolling window (default 5 minutes).
///   2. Trigger when notional in window exceeds <c>triggerNotional</c>:
///      • Long-side liquidations dominant → spot was forced down → fade BUY.
///      • Short-side liquidations dominant → spot was forced up → fade SELL.
///   3. Strength = notional / trigger; capped at 100.
/// </summary>
public static class LiquidationCascadeFader
{
    /// <summary>
    /// One liquidation event (typically pushed by an exchange WS channel).
    /// </summary>
    /// <param name="Timestamp">When the liquidation was reported.</param>
    /// <param name="Symbol">Perp symbol.</param>
    /// <param name="Side">Side that was liquidated:
    ///   • TradeSide.Buy = a long position was force-closed (sold at market) → bearish pressure.
    ///   • TradeSide.Sell = a short position was force-closed (bought at market) → bullish pressure.
    /// </param>
    /// <param name="NotionalUsd">USD notional of the liquidated position.</param>
    public sealed record LiquidationEvent(
        DateTime Timestamp,
        string Symbol,
        TradeSide Side,
        decimal NotionalUsd);

    /// <summary>
    /// A detected cascade and the implied fade trade.
    /// </summary>
    /// <param name="Symbol">Perp symbol the cascade hit.</param>
    /// <param name="WindowEnd">End timestamp of the aggregation window (= "now").</param>
    /// <param name="LongSideNotional">USD notional of long-side liquidations in the window.</param>
    /// <param name="ShortSideNotional">USD notional of short-side liquidations in the window.</param>
    /// <param name="DominantSide">Which side dominated the cascade (the side that was liquidated).</param>
    /// <param name="FadeSide">The trade direction implied by the cascade (opposite of DominantSide).</param>
    /// <param name="StrengthScore">0-100 confidence; saturates as notional ≫ trigger.</param>
    /// <param name="ExpectedHoldDuration">Suggested holding window for the fade (default 30 min).</param>
    public sealed record CascadeEvent(
        string Symbol,
        DateTime WindowEnd,
        decimal LongSideNotional,
        decimal ShortSideNotional,
        TradeSide DominantSide,
        TradeSide FadeSide,
        int StrengthScore,
        TimeSpan ExpectedHoldDuration);

    /// <summary>Default rolling-window length. 5 minutes covers a typical cascade duration.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);
    /// <summary>Default fade hold-duration. 30 min covers typical post-cascade reversion.</summary>
    public static readonly TimeSpan DefaultFadeHoldDuration = TimeSpan.FromMinutes(30);
    /// <summary>Default trigger notional for BTC: $5M in 5 min.</summary>
    public const decimal DefaultBtcTriggerNotional = 5_000_000m;
    /// <summary>Default trigger notional for alt assets: $1M in 5 min.</summary>
    public const decimal DefaultAltTriggerNotional = 1_000_000m;
    /// <summary>Required dominance ratio: dominant side ≥ 70% of total liquidation notional.</summary>
    public const double DefaultDominanceRatio = 0.70;

    /// <summary>
    /// Inspect a liquidation feed for a cascade ending at the latest event.
    /// Returns null if the rolling-window notional is below trigger or if no
    /// side has sufficient dominance.
    /// </summary>
    /// <param name="events">Liquidation events ordered oldest → newest.</param>
    /// <param name="symbol">Symbol label for the resulting <see cref="CascadeEvent"/>.</param>
    /// <param name="windowEnd">Window end timestamp (typically "now"). Window covers [windowEnd - window, windowEnd].</param>
    /// <param name="triggerNotional">Minimum total notional in the window to trigger.</param>
    /// <param name="window">Rolling window length.</param>
    /// <param name="dominanceRatio">Required share of dominant side in total liquidation notional.</param>
    public static CascadeEvent? Detect(
        IReadOnlyList<LiquidationEvent> events,
        string symbol,
        DateTime windowEnd,
        decimal triggerNotional,
        TimeSpan window = default,
        double dominanceRatio = DefaultDominanceRatio)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol must be non-empty.", nameof(symbol));
        if (triggerNotional <= 0)
            throw new ArgumentOutOfRangeException(nameof(triggerNotional));
        if (dominanceRatio <= 0.5 || dominanceRatio > 1.0)
            throw new ArgumentOutOfRangeException(nameof(dominanceRatio), "Must be in (0.5, 1.0].");

        var winLen = window == default ? DefaultWindow : window;
        var windowStart = windowEnd - winLen;

        decimal longNotional = 0m;
        decimal shortNotional = 0m;
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (!string.Equals(ev.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (ev.Timestamp < windowStart || ev.Timestamp > windowEnd) continue;
            if (ev.Side == TradeSide.Buy) longNotional += ev.NotionalUsd;
            else if (ev.Side == TradeSide.Sell) shortNotional += ev.NotionalUsd;
        }

        decimal total = longNotional + shortNotional;
        if (total < triggerNotional) return null;

        // Determine dominance.
        decimal dominantNotional;
        TradeSide dominant;
        if (longNotional >= shortNotional)
        {
            dominantNotional = longNotional;
            dominant = TradeSide.Buy;   // longs were liquidated
        }
        else
        {
            dominantNotional = shortNotional;
            dominant = TradeSide.Sell;
        }

        double dominanceFraction = total > 0 ? (double)(dominantNotional / total) : 0.0;
        if (dominanceFraction < dominanceRatio) return null;

        // Fade direction:
        //   • dominant = Buy  → long positions force-closed → market sold → price down → we BUY the dip.
        //   • dominant = Sell → short positions force-closed → market bought → price up → we SELL the spike.
        // The labels coincide (fadeSide == dominant) but the semantics are different
        // ("longs were liquidated" vs "we go long").
        TradeSide fadeSide = dominant == TradeSide.Buy ? TradeSide.Buy : TradeSide.Sell;

        // Strength: ratio of total notional to trigger, scaled to 0-100. Saturates at 5×.
        double strength = (double)(total / triggerNotional);
        int score = (int)Math.Round(Math.Min(100, strength * 20));

        return new CascadeEvent(
            Symbol: symbol,
            WindowEnd: windowEnd,
            LongSideNotional: longNotional,
            ShortSideNotional: shortNotional,
            DominantSide: dominant,
            FadeSide: fadeSide,
            StrengthScore: score,
            ExpectedHoldDuration: DefaultFadeHoldDuration);
    }
}
