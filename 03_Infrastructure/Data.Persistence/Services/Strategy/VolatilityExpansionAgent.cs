using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

/// <summary>
/// Regime-aware volatility expansion strategy — crypto straddle-like logic.
/// Profits when volatility explodes from compression.
///
/// 5-factor confluence:
///   1. Bollinger squeeze → expansion detection
///   2. ATR expansion — ATR(fast) must be accelerating vs ATR(slow)
///   3. Keltner-inside-Bollinger — classic TTM Squeeze
///   4. Expansion direction — first N bars after squeeze release determine direction
///   5. Volume confirmation — expansion must come with elevated volume
///
/// Scoring: base 5 per factor, bonuses for ATR ratio strength and direction clarity.
/// Minimum 3/5 factors required.
/// </summary>
public sealed class VolatilityExpansionAgent : IStrategyAgent
{
    private readonly ILogger<VolatilityExpansionAgent> _logger;
    private readonly VolatilityExpansionOptions _opts;

    public string StrategyTag => "VolExpansion";
    public int SwarmWeight => _opts.SwarmWeight;

    public VolatilityExpansionAgent(ILogger<VolatilityExpansionAgent> logger, IOptions<VolatilityExpansionOptions>? options = null)
    {
        _logger = logger;
        _opts = options?.Value ?? new VolatilityExpansionOptions();
    }

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Volatile or MarketRegime.Unknown or MarketRegime.Breakout;

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < _opts.MinCandles)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // ── Factor 1: Bollinger squeeze → expansion ──
        var (bbWidth, bbAvgWidth, bbPercentB) = ComputeBollingerMetrics(candles, _opts.BbPeriod, _opts.BbStdDevMultiplier);
        bool squeezeReleasing = bbAvgWidth > 0 && bbWidth > bbAvgWidth * _opts.BbWidthExpansionThreshold;
        bool wasSqueezed = patterns.BollingerSqueeze || (bbAvgWidth > 0 && bbWidth < bbAvgWidth * _opts.BbWidthSqueezeThreshold);

        // ── Factor 2: ATR expansion ──
        var atrFast = ComputeAtr(candles, _opts.AtrFastPeriod);
        var atrSlow = ComputeAtr(candles, _opts.AtrSlowPeriod);
        double atrRatio = atrSlow > 0 ? atrFast / atrSlow : 0;
        bool atrExpanding = atrRatio >= _opts.AtrExpansionThreshold;
        bool atrStrongExpanding = atrRatio >= _opts.AtrStrongExpansion;

        // ── Factor 3: Keltner-inside-Bollinger (TTM Squeeze) ──
        var (kcUpper, kcLower) = ComputeKeltnerChannels(candles, _opts.KcPeriod, _opts.KcAtrMultiplier);
        var (bbUpper, bbLower) = ComputeBollingerBands(candles, _opts.BbPeriod, _opts.BbStdDevMultiplier);
        bool kcInsideBb = kcUpper < bbUpper && kcLower > bbLower;
        bool wasSqueezeNowExpanding = !kcInsideBb && (wasSqueezed || squeezeReleasing);

        // ── Factor 4: Expansion direction ──
        var (direction, directionStrength) = DetectExpansionDirection(candles, _opts.DirectionBars);
        bool hasDirection = directionStrength >= _opts.DirectionThreshold;

        // ── Factor 5: Volume confirmation ──
        bool volumeExpanding = CheckVolumeExpansion(candles);

        // ── Confluence scoring ──
        int confirmedFactors = 0;
        var reasons = new List<string>(6);

        if (squeezeReleasing || wasSqueezed)
        {
            confirmedFactors++;
            var bwRatio = bbAvgWidth > 0 ? bbWidth / bbAvgWidth : 0;
            reasons.Add($"BB {(squeezeReleasing ? "expanding" : "squeezed")} {bwRatio:F2}x");
        }

        if (atrExpanding)
        {
            confirmedFactors++;
            reasons.Add($"ATR ratio={atrRatio:F2}x");
        }

        if (wasSqueezeNowExpanding || kcInsideBb)
        {
            confirmedFactors++;
            reasons.Add(kcInsideBb ? "TTM squeeze active" : "TTM squeeze releasing");
        }

        if (hasDirection)
        {
            confirmedFactors++;
            reasons.Add($"dir={direction} ({directionStrength:P0})");
        }

        if (volumeExpanding)
        {
            confirmedFactors++;
            reasons.Add("vol expanding");
        }

        // Need at least 3 of 5 factors
        if (confirmedFactors < 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        float confidence = confirmedFactors * _opts.BaseConfidencePerFactor;
        if (atrStrongExpanding) confidence += _opts.StrongAtrExpansionBonus;
        if (directionStrength > _opts.ClearDirectionThreshold) confidence += _opts.ClearDirectionBonus;

        if (!double.IsNaN(bbPercentB))
        {
            if (bbPercentB > 1.0 || bbPercentB < 0.0) confidence += _opts.ExtremePctBBonus;
        }

        var reasoning = string.Join(" | ", reasons);
        bool isBullish = direction == ExpansionDirection.Up;

        return Task.FromResult(isBullish
            ? StrategyVote.Approve(StrategyTag, confidence,
                $"Vol expansion {direction} ({confirmedFactors}/5 factors): {reasoning}", priorWeight: SwarmWeight)
            : StrategyVote.Reject(StrategyTag, confidence,
                $"Vol expansion {direction} ({confirmedFactors}/5 factors): {reasoning}", priorWeight: SwarmWeight));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Indicator Methods
    // ═══════════════════════════════════════════════════════════════════════

    internal enum ExpansionDirection { Up, Down, Neutral }

    /// <summary>
    /// Computes Bollinger Band width, its historical average, and %B.
    /// </summary>
    internal static (double CurrentWidth, double AvgWidth, double PercentB) ComputeBollingerMetrics(
        IReadOnlyList<Candle> candles, int period, double stdDevMultiplier = 2.0)
    {
        if (candles.Count < period * 2) return (0, 0, double.NaN);

        var recent = candles.TakeLast(period * 2).Select(c => (double)c.Close).ToArray();

        var currentSlice = recent[^period..];
        var (upper, lower, mid) = BollingerBand(currentSlice, stdDevMultiplier);
        double currentWidth = mid > 0 ? (upper - lower) / mid : 0;

        var priorSlice = recent[..period];
        var (pUpper, pLower, pMid) = BollingerBand(priorSlice, stdDevMultiplier);
        double priorWidth = pMid > 0 ? (pUpper - pLower) / pMid : 0;

        double avgWidth = (currentWidth + priorWidth) / 2.0;

        // %B: position of current price relative to bands
        double lastClose = (double)candles[^1].Close;
        double percentB = (upper - lower) > 0 ? (lastClose - lower) / (upper - lower) : 0.5;

        return (currentWidth, avgWidth, percentB);
    }

    /// <summary>
    /// Average True Range over the given period.
    /// </summary>
    internal static double ComputeAtr(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period + 1) return 0;

        var data = candles.TakeLast(period + 1).ToList();
        double sum = 0;
        for (int i = 1; i < data.Count; i++)
        {
            double hl = (double)(data[i].High - data[i].Low);
            double hc = Math.Abs((double)(data[i].High - data[i - 1].Close));
            double lc = Math.Abs((double)(data[i].Low - data[i - 1].Close));
            sum += Math.Max(hl, Math.Max(hc, lc));
        }
        return sum / period;
    }

    /// <summary>
    /// Keltner Channel upper and lower bands: EMA ± multiplier × ATR.
    /// </summary>
    internal static (double Upper, double Lower) ComputeKeltnerChannels(
        IReadOnlyList<Candle> candles, int period, double atrMultiplier)
    {
        if (candles.Count < period + 1) return (0, 0);

        var data = candles.TakeLast(period + 1).ToList();

        // Simple EMA approximation (SMA for stability)
        double sma = data.TakeLast(period).Average(c => (double)c.Close);
        double atr = ComputeAtr(candles, period);

        return (sma + atrMultiplier * atr, sma - atrMultiplier * atr);
    }

    /// <summary>
    /// Bollinger Bands: SMA ± multiplier × stddev.
    /// </summary>
    internal static (double Upper, double Lower) ComputeBollingerBands(
        IReadOnlyList<Candle> candles, int period, double multiplier)
    {
        if (candles.Count < period) return (0, 0);

        var closes = candles.TakeLast(period).Select(c => (double)c.Close).ToArray();
        var (upper, lower, _) = BollingerBand(closes, multiplier);
        return (upper, lower);
    }

    /// <summary>
    /// Detects the direction of the volatility expansion from the last N bars.
    /// </summary>
    internal static (ExpansionDirection Direction, double Strength) DetectExpansionDirection(
        IReadOnlyList<Candle> candles, int lookback)
    {
        if (candles.Count <= lookback) return (ExpansionDirection.Neutral, 0);

        int upBars = 0, downBars = 0;
        for (int i = Math.Max(candles.Count - lookback, 1); i < candles.Count; i++)
        {
            if (candles[i].Close > candles[i - 1].Close) upBars++;
            else downBars++;
        }

        int total = upBars + downBars;
        if (total == 0) return (ExpansionDirection.Neutral, 0);

        double strength = (double)Math.Max(upBars, downBars) / total;
        var dir = upBars > downBars ? ExpansionDirection.Up : ExpansionDirection.Down;

        return (dir, strength);
    }

    private bool CheckVolumeExpansion(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < _opts.VolAvgBars + 3) return false;

        var avgVol = candles.TakeLast(_opts.VolAvgBars + 3).Take(_opts.VolAvgBars).Average(c => c.Volume);
        var recentAvg = candles.TakeLast(3).Average(c => c.Volume);

        return avgVol > 0 && recentAvg > avgVol * (decimal)_opts.VolExpansionMultiple;
    }

    private static (double Upper, double Lower, double Mid) BollingerBand(double[] closes, double multiplier)
    {
        double mean = closes.Average();
        double variance = closes.Select(c => (c - mean) * (c - mean)).Average();
        double stdDev = Math.Sqrt(variance);
        return (mean + multiplier * stdDev, mean - multiplier * stdDev, mean);
    }
}
