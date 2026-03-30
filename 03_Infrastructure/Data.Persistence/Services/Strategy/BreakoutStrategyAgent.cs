using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

public sealed class BreakoutStrategyAgent : IStrategyAgent
{
    private readonly ILogger<BreakoutStrategyAgent> _logger;
    private readonly BreakoutOptions _options;

    public string StrategyTag => "Breakout";
    public int SwarmWeight => _options.SwarmWeight;

    public BreakoutStrategyAgent(ILogger<BreakoutStrategyAgent> logger, IOptions<BreakoutOptions>? options = null)
    {
        _logger = logger;
        _options = options?.Value ?? new BreakoutOptions();
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
        if (candles.Count < _options.LookbackBars + 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var window = candles.TakeLast(_options.LookbackBars).ToList();
        var swingHigh = window.Max(c => c.High);
        var swingLow = window.Min(c => c.Low);
        var range = swingHigh - swingLow;
        if (range <= 0)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var fibBull = swingLow + range * (decimal)_options.FibBullLevel;
        var fibBear = swingLow + range * (decimal)_options.FibBearLevel;
        var isBullish = signal.Side != TradeSide.Sell;
        var breakoutLevel = isBullish ? fibBull : fibBear;
        var priceBrokeOut = isBullish ? signal.Price > breakoutLevel : signal.Price < breakoutLevel;
        if (!priceBrokeOut)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var staleBars = CountBarsFromSwingExtreme(window, isBullish ? swingHigh : swingLow, isBullish);
        if (staleBars > _options.MaxSwingStaleBars)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var avgVol = window.TakeLast(_options.VolumeAvgBars).Average(c => c.Volume);
        var lastCandle = candles[^1];
        var volumeSurge = avgVol > 0 && lastCandle.Volume > avgVol * (decimal)_options.VolumeSurgeMultiple;
        if (!volumeSurge)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var flowConfirmed = isBullish
            ? foundation.BidAskImbalance > 0.10
            : foundation.BidAskImbalance < -0.10;
        if (!flowConfirmed)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var (baseDuration, baseQuality) = MeasureBase(window, swingHigh, swingLow, (decimal)_options.RangeThresholdPercent);
        var retestConfirmed = CheckRetest(candles, breakoutLevel, isBullish, (decimal)_options.RetestProximityPercent, _options.RetestLookbackBars);
        var thinAir = CheckThinAir(window, swingLow, range, isBullish, _options.VolumeProfileBins, (decimal)_options.ThinAirThreshold);

        var confirmedFactors = 2;
        var reasons = new List<string>
        {
            isBullish ? $"price > fib {_options.FibBullLevel:F3}" : $"price < fib {_options.FibBearLevel:F3}",
            $"vol surge {lastCandle.Volume / avgVol:F1}x",
            $"OFI={foundation.BidAskImbalance:F2}"
        };

        if (baseDuration >= _options.MinConsolidationBars)
        {
            confirmedFactors++;
            reasons.Add($"base {baseDuration} bars");
        }

        if (retestConfirmed)
        {
            confirmedFactors++;
            reasons.Add("retest confirmed");
        }

        if (thinAir)
        {
            confirmedFactors++;
            reasons.Add(isBullish ? "thin air above" : "thin air below");
        }

        if (confirmedFactors < 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        float confidence = confirmedFactors * _options.BaseConfidencePerFactor;
        if (baseDuration >= _options.LongBase) confidence += _options.LongBaseDurationBonus;
        else if (baseDuration >= _options.MediumBase) confidence += _options.MediumBaseDurationBonus;
        if (thinAir) confidence += _options.ThinAirBonus;
        if (baseQuality > (decimal)_options.TightConsolidationQuality) confidence += _options.TightConsolidationBonus;

        var reasoning = string.Join(" | ", reasons);
        return Task.FromResult(isBullish
            ? StrategyVote.Approve(StrategyTag, confidence, $"Breakout ({confirmedFactors}/5 factors): {reasoning}")
            : StrategyVote.Reject(StrategyTag, confidence, $"Breakdown ({confirmedFactors}/5 factors): {reasoning}"));
    }

    private static int CountBarsFromSwingExtreme(IReadOnlyList<Candle> window, decimal swingExtreme, bool bullish)
    {
        var bars = 0;
        for (var i = window.Count - 1; i >= 0; i--)
        {
            var price = bullish ? window[i].High : window[i].Low;
            if (Math.Abs(price - swingExtreme) <= Math.Abs(swingExtreme) * 0.0001m)
                break;

            bars++;
        }

        return bars;
    }

    private static (int Duration, decimal Quality) MeasureBase(IReadOnlyList<Candle> window, decimal swingHigh, decimal swingLow, decimal rangeThresholdPct)
    {
        var range = swingHigh - swingLow;
        if (range <= 0) return (0, 0m);

        var midpoint = (swingHigh + swingLow) / 2m;
        var threshold = range * rangeThresholdPct * 10m;
        var inRange = 0;
        var total = 0;

        for (var i = window.Count - 2; i >= 0; i--)
        {
            var distance = Math.Abs(window[i].Close - midpoint);
            if (distance <= threshold)
            {
                inRange++;
                total++;
            }
            else if (total > 0)
            {
                break;
            }
        }

        return (inRange, total > 0 ? (decimal)inRange / total : 0m);
    }

    private static bool CheckRetest(IReadOnlyList<Candle> candles, decimal level, bool bullish, decimal proximityPct, int lookbackBars)
    {
        if (candles.Count < lookbackBars + 2) return false;

        var proximity = Math.Abs(level) * proximityPct;
        var touched = false;
        var resumed = false;
        var start = Math.Max(0, candles.Count - 1 - lookbackBars);

        for (var i = start; i < candles.Count - 1; i++)
        {
            if (bullish)
            {
                if (Math.Abs(candles[i].Low - level) <= proximity || (candles[i].Low <= level && candles[i].Close >= level))
                    touched = true;
                if (touched && candles[i].Close > level + proximity)
                {
                    resumed = true;
                    break;
                }
            }
            else
            {
                if (Math.Abs(candles[i].High - level) <= proximity || (candles[i].High >= level && candles[i].Close <= level))
                    touched = true;
                if (touched && candles[i].Close < level - proximity)
                {
                    resumed = true;
                    break;
                }
            }
        }

        return touched && resumed;
    }

    private static bool CheckThinAir(IReadOnlyList<Candle> window, decimal swingLow, decimal range, bool bullish, int bins, decimal threshold)
    {
        if (range <= 0 || window.Count == 0 || bins < 2) return false;

        var binSize = range / bins;
        if (binSize <= 0) return false;

        var binVolumes = new decimal[bins];
        foreach (var candle in window)
        {
            var index = (int)((candle.Close - swingLow) / binSize);
            index = Math.Clamp(index, 0, bins - 1);
            binVolumes[index] += candle.Volume;
        }

        var total = binVolumes.Sum();
        if (total <= 0) return false;

        var average = total / bins;
        if (average <= 0) return false;

        var start = bullish ? bins - 2 : 0;
        var end = bullish ? bins : 2;
        decimal zoneAverage = 0m;
        for (var i = start; i < end; i++)
            zoneAverage += binVolumes[i];

        zoneAverage /= 2m;
        return zoneAverage < average * threshold;
    }
}
