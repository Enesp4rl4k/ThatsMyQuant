using System;
using System.Collections.Generic;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Detects "stop hunt" patterns on a single asset's recent candles. A stop hunt is
/// a brief price excursion past a known liquidity zone (recent swing high/low,
/// round number, common technical level) that triggers a cluster of stops, then
/// rapidly reverses — leaving a long wick on the bar. The mechanism is well-
/// documented in market-microstructure literature: large players sweep stop
/// liquidity to absorb their own positioning needs, and the resulting
/// over-extension reverts within a few bars.
///
/// Two distinct uses for the same detection:
///
///   1. <b>Defensive (Phase I4):</b> When our position's stop-loss is near the
///      detected zone and the wick reaches but doesn't break sustainably, hold
///      the position rather than executing the stop — the wick is suspect.
///
///   2. <b>Offensive (Phase D5):</b> The detection IS the entry signal — fade
///      the hunt by going counter-direction to the wick, since post-hunt
///      reversal is empirically reliable (~30-50bp/event, WR 60-70%).
///
/// Detection criteria (all must hold on the same candle):
///   • Bar wick (high-close or close-low) is at least <c>WickAtrMultiple</c> ×
///     ATR — meaningfully large relative to current volatility.
///   • The wick extreme breaches a "key level" (caller-supplied or auto-detected
///     from recent swing highs/lows in the lookback window).
///   • The bar closes BACK on the original side of the level — i.e., breach
///     was reversed within the same bar (the hunt failed to hold).
///   • Volume on the wick bar is at or above the rolling median (real activity,
///     not a thin-tape print).
///
/// Strength scoring 0-100:
///   wickRatioScore (max 40)  +  closeReversalScore (max 30)
///   + volumeConfirmScore (max 20)  +  keyLevelClosenessScore (max 10).
/// </summary>
public static class StopHuntDetector
{
    public const int DefaultLookbackBars = 50;
    public const double DefaultWickAtrMultiple = 0.5;
    public const int DefaultAtrPeriod = 14;
    public const decimal DefaultVolumeFloorMultiplier = 1.0m;
    public const decimal DefaultKeyLevelTolerancePercent = 0.001m;  // 0.10%

    /// <summary>
    /// Detected stop-hunt event with direction and strength score.
    /// </summary>
    /// <param name="HuntDirection">Direction the wick pointed:
    ///   • Buy = upward wick (high &gt;&gt; close, swept buy-side stops above) → fade SELL.
    ///   • Sell = downward wick (low &lt;&lt; close, swept sell-side stops below) → fade BUY.
    /// </param>
    /// <param name="FadeSide">Counter-trade direction (opposite of HuntDirection).</param>
    /// <param name="WickFraction">|wick| / ATR — strength of the wick relative to volatility.</param>
    /// <param name="LevelBreached">Estimate of the key level the wick swept (recent swing).</param>
    /// <param name="StrengthScore">0-100 composite confidence for fading.</param>
    public sealed record StopHuntEvent(
        TradeSide HuntDirection,
        TradeSide FadeSide,
        double WickFraction,
        decimal LevelBreached,
        int StrengthScore,
        DateTime Timestamp);

    /// <summary>
    /// Inspect the LATEST bar in <paramref name="candles"/> for a stop-hunt pattern.
    /// Returns null when no qualifying pattern is found.
    /// </summary>
    /// <param name="candles">Bars ordered oldest → newest. Need at least <paramref name="lookbackBars"/> + atrPeriod bars.</param>
    /// <param name="lookbackBars">Window for swing-high/low detection. Default 50.</param>
    /// <param name="wickAtrMultiple">Minimum |wick| / ATR. Default 0.5.</param>
    /// <param name="atrPeriod">Bars used for ATR. Default 14.</param>
    /// <param name="volumeFloorMultiplier">|wick bar volume| must be ≥ this × median(volume_lookback).</param>
    /// <param name="keyLevelTolerance">Fraction of price; how close the wick high/low must be
    /// to the swing extreme to count as "sweeping" it. Default 0.10%.</param>
    public static StopHuntEvent? DetectOnLatestBar(
        IReadOnlyList<Candle> candles,
        int lookbackBars = DefaultLookbackBars,
        double wickAtrMultiple = DefaultWickAtrMultiple,
        int atrPeriod = DefaultAtrPeriod,
        decimal volumeFloorMultiplier = DefaultVolumeFloorMultiplier,
        decimal keyLevelTolerance = DefaultKeyLevelTolerancePercent)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count < lookbackBars + atrPeriod + 2) return null;
        if (wickAtrMultiple <= 0) throw new ArgumentOutOfRangeException(nameof(wickAtrMultiple));
        if (atrPeriod < 2) throw new ArgumentOutOfRangeException(nameof(atrPeriod));

        int latest = candles.Count - 1;
        var bar = candles[latest];

        // 1. ATR over the most recent atrPeriod bars.
        double atr = ComputeAtr(candles, atrPeriod);
        if (atr <= 0) return null;

        // 2. Wick analysis on the latest bar. The close-position-within-range gate is
        //    stronger than a simple close-vs-open color check: a real stop hunt has the
        //    close pull back into the OPPOSITE half of the bar from the wick extreme,
        //    regardless of whether the bar happens to close green or red overall.
        decimal upperWick = bar.High - Math.Max(bar.Open, bar.Close);
        decimal lowerWick = Math.Min(bar.Open, bar.Close) - bar.Low;
        double upperWickFraction = atr > 0 ? (double)upperWick / atr : 0;
        double lowerWickFraction = atr > 0 ? (double)lowerWick / atr : 0;
        decimal range = bar.High - bar.Low;
        double closePosition = range > 0 ? (double)((bar.Close - bar.Low) / range) : 0.5;

        bool upperHunt = upperWickFraction >= wickAtrMultiple && closePosition < 0.5;
        bool lowerHunt = lowerWickFraction >= wickAtrMultiple && closePosition > 0.5;
        if (!upperHunt && !lowerHunt) return null;

        // 3. Swing levels in the lookback window (excluding the latest bar to avoid
        //    self-reference).
        int windowStart = Math.Max(0, latest - lookbackBars);
        decimal swingHigh = decimal.MinValue;
        decimal swingLow = decimal.MaxValue;
        for (int k = windowStart; k < latest; k++)
        {
            if (candles[k].High > swingHigh) swingHigh = candles[k].High;
            if (candles[k].Low < swingLow) swingLow = candles[k].Low;
        }

        // 4. Key-level proximity check.
        decimal tolerance = bar.Close * keyLevelTolerance;
        bool sweptUpper = upperHunt && bar.High >= swingHigh - tolerance;
        bool sweptLower = lowerHunt && bar.Low <= swingLow + tolerance;
        if (!sweptUpper && !sweptLower) return null;

        // 5. Volume confirmation — wick should ride a real spike, not thin tape.
        decimal medianVolume = ComputeMedianVolume(candles, lookbackBars);
        bool volumeConfirms = medianVolume <= 0 || bar.Volume >= medianVolume * volumeFloorMultiplier;
        if (!volumeConfirms) return null;

        // 6. Composite strength score.
        TradeSide huntDir = sweptUpper ? TradeSide.Buy : TradeSide.Sell;
        TradeSide fadeSide = huntDir == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        double dominantWickFraction = sweptUpper ? upperWickFraction : lowerWickFraction;
        decimal levelBreached = sweptUpper ? swingHigh : swingLow;

        int score = ScoreStopHunt(
            wickFraction: dominantWickFraction,
            closeReversalFraction: ComputeCloseReversalFraction(bar, huntDir),
            volumeRatio: medianVolume > 0 ? (double)(bar.Volume / medianVolume) : 1.0,
            levelClosenessFraction: ComputeLevelClosenessFraction(bar, levelBreached, huntDir));

        return new StopHuntEvent(
            HuntDirection: huntDir,
            FadeSide: fadeSide,
            WickFraction: dominantWickFraction,
            LevelBreached: levelBreached,
            StrengthScore: score,
            Timestamp: bar.Timestamp);
    }

    private static double ComputeAtr(IReadOnlyList<Candle> candles, int period)
    {
        int latest = candles.Count - 1;
        int start = Math.Max(1, latest - period + 1);
        double sum = 0;
        int count = 0;
        for (int i = start; i <= latest; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            double tr = Math.Max(
                (double)(c.High - c.Low),
                Math.Max(
                    Math.Abs((double)(c.High - prevClose)),
                    Math.Abs((double)(c.Low - prevClose))));
            sum += tr;
            count++;
        }
        return count > 0 ? sum / count : 0;
    }

    private static decimal ComputeMedianVolume(IReadOnlyList<Candle> candles, int lookback)
    {
        int latest = candles.Count - 1;
        int start = Math.Max(0, latest - lookback);
        int len = latest - start;
        if (len <= 0) return 0m;
        var arr = new decimal[len];
        for (int i = 0; i < len; i++) arr[i] = candles[start + i].Volume;
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    private static double ComputeCloseReversalFraction(Candle bar, TradeSide huntDir)
    {
        // Fraction of the bar's range that the close "reversed" away from the wick extreme.
        decimal range = bar.High - bar.Low;
        if (range <= 0) return 0.0;
        decimal reverseDistance = huntDir == TradeSide.Buy
            ? bar.High - bar.Close
            : bar.Close - bar.Low;
        return (double)(reverseDistance / range);
    }

    private static double ComputeLevelClosenessFraction(Candle bar, decimal level, TradeSide huntDir)
    {
        // How close the wick extreme is to the level, in absolute fraction of price.
        decimal extreme = huntDir == TradeSide.Buy ? bar.High : bar.Low;
        decimal distance = Math.Abs(extreme - level);
        return bar.Close > 0 ? (double)(distance / bar.Close) : 0;
    }

    private static int ScoreStopHunt(
        double wickFraction,
        double closeReversalFraction,
        double volumeRatio,
        double levelClosenessFraction)
    {
        // Wick component (max 40):
        //   2.0 ATR → 40, 1.0 ATR → 25, 0.5 ATR → 10
        int wickScore = wickFraction >= 2.0 ? 40
                      : wickFraction >= 1.5 ? 32
                      : wickFraction >= 1.0 ? 25
                      : wickFraction >= 0.7 ? 16
                      : 10;

        // Close-reversal component (max 30): how decisively the bar closed back.
        int reversalScore = closeReversalFraction >= 0.80 ? 30
                          : closeReversalFraction >= 0.60 ? 22
                          : closeReversalFraction >= 0.40 ? 14
                          : 6;

        // Volume confirmation (max 20).
        int volumeScore = volumeRatio >= 2.5 ? 20
                        : volumeRatio >= 1.5 ? 14
                        : volumeRatio >= 1.0 ? 8
                        : 3;

        // Level-closeness (max 10): smaller fraction = wick swept exactly the level.
        int closenessScore = levelClosenessFraction <= 0.0005 ? 10
                            : levelClosenessFraction <= 0.0010 ? 7
                            : levelClosenessFraction <= 0.0020 ? 4
                            : 1;

        return Math.Clamp(wickScore + reversalScore + volumeScore + closenessScore, 0, 100);
    }
}
