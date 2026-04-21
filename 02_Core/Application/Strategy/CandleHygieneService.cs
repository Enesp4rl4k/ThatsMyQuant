using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Data-hygiene checks applied before a candle series is handed to backtest or paper pipelines.
/// Three checks:
///   1. Gap filter — candles with |Open/PrevClose - 1| above threshold are flagged as flash
///      events. In crypto (24/7, no overnight gap) a >5% gap is almost always a data issue
///      (exchange halt, stitched cross-venue feed) or a liquidation cascade that a
///      mean-reverting backtest cannot honestly replay.
///   2. Zero/negative price detection — broken feeds occasionally emit 0 or NaN candles.
///   3. Survivorship/delisting advisory — if history ends earlier than requested, warn.
///
/// Caller policy: flag and optionally drop. Dropping is safer for PnL honesty; keeping and
/// tagging is safer when testing robustness to real shock bars.
/// </summary>
public static class CandleHygieneService
{
    public const decimal DefaultMaxGapFraction = 0.05m;

    public sealed record HygieneReport(
        int InputCount,
        int DroppedZeroPrice,
        int FlaggedGaps,
        IReadOnlyList<(DateTime Timestamp, decimal GapFraction)> Gaps,
        DateTime? FirstTimestamp,
        DateTime? LastTimestamp);

    /// <summary>
    /// Scans and optionally filters. Always drops zero/negative price candles (unconditionally
    /// invalid). Gaps are flagged; drop when <paramref name="dropGaps"/> is true.
    /// </summary>
    public static (List<Candle> Clean, HygieneReport Report) Sanitize(
        IReadOnlyList<Candle> input,
        decimal maxGapFraction = DefaultMaxGapFraction,
        bool dropGaps = false,
        ILogger? logger = null)
    {
        var clean = new List<Candle>(input.Count);
        var gaps = new List<(DateTime, decimal)>();
        int droppedZero = 0;

        for (int i = 0; i < input.Count; i++)
        {
            var c = input[i];
            if (c.Open <= 0 || c.High <= 0 || c.Low <= 0 || c.Close <= 0)
            {
                droppedZero++;
                logger?.LogWarning("CandleHygiene: drop zero/neg price at {Ts}", c.Timestamp);
                continue;
            }
            if (clean.Count > 0)
            {
                var prev = clean[^1];
                if (prev.Close > 0)
                {
                    var gap = Math.Abs(c.Open / prev.Close - 1m);
                    if (gap > maxGapFraction)
                    {
                        gaps.Add((c.Timestamp, gap));
                        logger?.LogWarning("CandleHygiene: gap {Pct:P2} at {Ts} ({Prev}->{Open})",
                            (double)gap, c.Timestamp, prev.Close, c.Open);
                        if (dropGaps) continue;
                    }
                }
            }
            clean.Add(c);
        }

        var report = new HygieneReport(
            InputCount: input.Count,
            DroppedZeroPrice: droppedZero,
            FlaggedGaps: gaps.Count,
            Gaps: gaps,
            FirstTimestamp: clean.Count > 0 ? clean[0].Timestamp : null,
            LastTimestamp: clean.Count > 0 ? clean[^1].Timestamp : null);

        return (clean, report);
    }

    /// <summary>
    /// Survivorship advisory: if the fetched history ends meaningfully before the requested range,
    /// return true and a reason. Caller decides whether to treat as delisting or plain data-gap.
    /// </summary>
    public static bool IsPotentiallyDelisted(IReadOnlyList<Candle> candles, DateTime requestedEndUtc, TimeSpan tolerance, out string reason)
    {
        if (candles.Count == 0)
        {
            reason = "No candles returned for requested range.";
            return true;
        }
        var last = candles[^1].Timestamp;
        if (requestedEndUtc - last > tolerance)
        {
            reason = $"History ends {last:u} but requested {requestedEndUtc:u} (gap {requestedEndUtc - last}).";
            return true;
        }
        reason = "";
        return false;
    }
}
