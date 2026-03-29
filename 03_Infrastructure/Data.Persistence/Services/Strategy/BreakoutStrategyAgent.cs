using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

/// <summary>
/// Enhanced breakout strategy using 5-factor confluence:
///   1. Fib 0.786 breakout — price must clear the key retracement level
///   2. Volume surge — breakout candle volume > 1.5× rolling average
///   3. Base duration — longer consolidation before breakout = stronger signal
///   4. Re-test confirmation — price pulled back toward range top then resumed
///   5. False breakout filter — volume profile must show thin air above resistance
///
/// Scoring: base 6 per confirmed factor. Base duration and volume profile add bonus.
/// Minimum 3/5 factors required for a signal.
/// </summary>
public sealed class BreakoutStrategyAgent : IStrategyAgent
{
    private readonly ILogger<BreakoutStrategyAgent> _logger;
    private readonly BreakoutOptions _opts;

    public string StrategyTag => "Breakout";
    public int SwarmWeight => _opts.SwarmWeight;

    public BreakoutStrategyAgent(ILogger<BreakoutStrategyAgent> logger, IOptions<BreakoutOptions>? options = null)
    {
        _logger = logger;
        _opts = options?.Value ?? new BreakoutOptions();
    }

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Volatile;

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < _opts.LookbackBars + 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var window = candles.TakeLast(_opts.LookbackBars).ToList();

        // ── Swing range ──
        var swingHigh = window.Max(c => c.High);
        var swingLow = window.Min(c => c.Low);
        var range = swingHigh - swingLow;
        if (range <= 0)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var fib786 = swingLow + range * (decimal)_opts.FibRetracementLevel;

        // ── Factor 1: Fib 0.786 breakout ──
        bool priceAboveFib = signal.Price > fib786;
        if (!priceAboveFib)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // ── Swing freshness guard: reject stale swings (> 40 bars old) ──
        int barsFromSwingHigh = 0;
        for (int i = window.Count - 1; i >= 0; i--)
        {
            if (Math.Abs(window[i].High - swingHigh) < swingHigh * 0.0001m) break;
            barsFromSwingHigh++;
        }
        if (barsFromSwingHigh > _opts.MaxSwingHighStaleBars)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // ── Factor 2: Volume surge (MANDATORY — breakout without volume is 70% likely to fail) ──
        var avgVol = window.TakeLast(_opts.VolumeAvgBars).Average(c => c.Volume);
        var lastCandle = candles[^1];
        bool volumeSurge = avgVol > 0 && lastCandle.Volume > avgVol * (decimal)_opts.VolumeSurgeMultiple;
        if (!volumeSurge)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // ── Factor 3: Base duration ──
        // Count consecutive bars before the breakout bar where price stayed within the range body
        var (baseDuration, baseQuality) = MeasureBaseDuration(window, swingHigh, swingLow, (decimal)_opts.RangeThresholdPercent);

        // ── Factor 4: Re-test confirmation ──
        bool retestConfirmed = CheckRetestConfirmation(candles, fib786, (decimal)_opts.RetestProximityPercent, _opts.RetestLookbackBars);

        // ── Factor 5: False breakout filter via volume profile ──
        bool thinAirAbove = CheckThinAirAboveResistance(window, swingLow, range, _opts.VolumeProfileBins, (decimal)_opts.ThinAirThreshold);

        // ── Confluence scoring ──
        int confirmedFactors = 1; // Fib breakout is the base factor
        var reasons = new List<string>(6) { $"Price > 0.786 Fib ({fib786:F2})" };

        // Volume is mandatory — always counted
        confirmedFactors++;
        reasons.Add($"vol surge {lastCandle.Volume / avgVol:F1}x");

        if (baseDuration >= _opts.MinConsolidationBars)
        {
            confirmedFactors++;
            reasons.Add($"base {baseDuration} bars");
        }

        if (retestConfirmed)
        {
            confirmedFactors++;
            reasons.Add("re-test confirmed");
        }

        if (thinAirAbove)
        {
            confirmedFactors++;
            reasons.Add("thin air above");
        }

        // Need at least 3 of 5 factors
        if (confirmedFactors < 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        float confidence = confirmedFactors * _opts.BaseConfidencePerFactor;

        if (baseDuration >= _opts.LongBase) confidence += _opts.LongBaseDurationBonus;
        else if (baseDuration >= _opts.MediumBase) confidence += _opts.MediumBaseDurationBonus;

        if (thinAirAbove) confidence += _opts.ThinAirBonus;

        if (baseQuality > (decimal)_opts.TightConsolidationQuality) confidence += _opts.TightConsolidationBonus;

        var reasoning = string.Join(" | ", reasons);
        return Task.FromResult(StrategyVote.Approve(StrategyTag, confidence,
            $"Breakout ({confirmedFactors}/5 factors): {reasoning}", priorWeight: SwarmWeight));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Analysis Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Counts bars in the consolidation base before the breakout.
    /// A bar is "in-range" if its close is within the middle portion of the swing range.
    /// Returns (duration, quality) where quality = fraction of bars that were in-range.
    /// </summary>
    internal static (int Duration, decimal Quality) MeasureBaseDuration(
        IReadOnlyList<Candle> window, decimal swingHigh, decimal swingLow, decimal rangeThresholdPct = 0.03m)
    {
        var range = swingHigh - swingLow;
        if (range <= 0) return (0, 0m);

        var midpoint = (swingHigh + swingLow) / 2m;
        var threshold = range * rangeThresholdPct * 10;

        int inRangeCount = 0;
        int totalCounted = 0;

        // Walk backward from second-to-last bar (last bar is the breakout bar)
        for (int i = window.Count - 2; i >= 0; i--)
        {
            var distFromMid = Math.Abs(window[i].Close - midpoint);
            if (distFromMid <= threshold)
            {
                inRangeCount++;
                totalCounted++;
            }
            else if (totalCounted > 0)
            {
                // First bar that breaks out of the range ends the base
                break;
            }
        }

        decimal quality = totalCounted > 0 ? (decimal)inRangeCount / totalCounted : 0m;
        return (inRangeCount, quality);
    }

    /// <summary>
    /// Checks if price pulled back toward the breakout level (fib786) in recent bars
    /// and then resumed upward — a classic re-test confirmation pattern.
    /// </summary>
    internal static bool CheckRetestConfirmation(IReadOnlyList<Candle> candles, decimal fib786, decimal retestProximityPct = 0.005m, int retestLookbackBars = 5)
    {
        if (candles.Count < retestLookbackBars + 2) return false;

        var proximity = fib786 * retestProximityPct;
        bool touchedLevel = false;
        bool resumedUp = false;

        int start = candles.Count - 1 - retestLookbackBars;
        if (start < 0) start = 0;

        for (int i = start; i < candles.Count - 1; i++)
        {
            var low = candles[i].Low;
            // Bar's low came within proximity of fib786 from above (pullback)
            if (Math.Abs(low - fib786) <= proximity || (low <= fib786 && candles[i].Close >= fib786))
            {
                touchedLevel = true;
            }

            // After touching, did a subsequent bar close above the level?
            if (touchedLevel && candles[i].Close > fib786 + proximity)
            {
                resumedUp = true;
                break;
            }
        }

        return touchedLevel && resumedUp;
    }

    /// <summary>
    /// Builds a simple volume profile across the swing range and checks whether
    /// the zone above the 0.786 fib level has notably less volume ("thin air").
    /// Low volume above resistance means less supply to absorb the breakout.
    /// </summary>
    internal static bool CheckThinAirAboveResistance(
        IReadOnlyList<Candle> window, decimal swingLow, decimal range, int volumeProfileBins = 10, decimal thinAirThreshold = 0.5m)
    {
        if (range <= 0 || window.Count == 0) return false;

        var binSize = range / volumeProfileBins;
        if (binSize <= 0) return false;

        var binVolumes = new decimal[volumeProfileBins];

        foreach (var candle in window)
        {
            var binIndex = (int)((candle.Close - swingLow) / binSize);
            if (binIndex < 0) binIndex = 0;
            if (binIndex >= volumeProfileBins) binIndex = volumeProfileBins - 1;
            binVolumes[binIndex] += candle.Volume;
        }

        var totalVolume = binVolumes.Sum();
        if (totalVolume <= 0) return false;
        var avgBinVolume = totalVolume / volumeProfileBins;

        int topBinStart = volumeProfileBins - 2;
        decimal topZoneAvg = 0;
        for (int i = topBinStart; i < volumeProfileBins; i++)
            topZoneAvg += binVolumes[i];
        topZoneAvg /= 2;

        // "Thin air" = volume in breakout zone is less than threshold × average
        return topZoneAvg < avgBinVolume * thinAirThreshold;
    }
}
