using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

/// <summary>
/// Enhanced momentum strategy using 4-factor confluence:
///   1. RSI(14) — primary momentum oscillator
///   2. ADX(14) — trend strength filter (must be > 20 to confirm a real trend)
///   3. Multi-timeframe RSI — RSI on synthetic 4x candles must agree with primary RSI
///   4. EMA(9)/EMA(21) crossover — confirms trend direction via moving average alignment
///
/// Scoring: base 8 confidence per confirmed factor (max 32 with all 4 + volume).
/// Factors that fail reduce confidence but don't veto if RSI + at least one other factor agree.
/// </summary>
public sealed class MomentumStrategyAgent : IStrategyAgent
{
    private readonly ILogger<MomentumStrategyAgent> _logger;
    private readonly MomentumOptions _opts;

    public string StrategyTag => "Momentum";
    public int SwarmWeight => _opts.SwarmWeight;

    public MomentumStrategyAgent(ILogger<MomentumStrategyAgent> logger, IOptions<MomentumOptions>? options = null)
    {
        _logger = logger;
        _opts = options?.Value ?? new MomentumOptions();
    }

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Trending;

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < _opts.MinCandles)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // ── Factor 1: RSI ──
        var rsi = ComputeRsi(candles, _opts.RsiPeriod);
        bool rsiBullish = rsi > _opts.RsiBullThreshold;
        bool rsiBearish = rsi < _opts.RsiBearThreshold;

        if (!rsiBullish && !rsiBearish)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // ── Factor 2: ADX trend strength ──
        var adx = ComputeAdx(candles, _opts.AdxPeriod);
        bool hasTrend = adx >= _opts.AdxTrendThreshold;
        bool strongTrend = adx >= _opts.AdxStrongThreshold;

        // ── Factor 3: Multi-timeframe RSI ──
        var htfCandles = AggregateCandles(candles, _opts.MultiTfAggFactor);
        bool mtfConfirmed = false;
        if (htfCandles.Count >= _opts.RsiPeriod + 1)
        {
            var htfRsi = ComputeRsi(htfCandles, _opts.RsiPeriod);
            mtfConfirmed = (rsiBullish && htfRsi > _opts.MultiTfRsiMidpoint) || (rsiBearish && htfRsi < _opts.MultiTfRsiMidpoint);
        }

        // ── Factor 4: EMA crossover ──
        var emaFast = ComputeEma(candles, _opts.EmaFastPeriod);
        var emaSlow = ComputeEma(candles, _opts.EmaSlowPeriod);
        bool emaBullCross = emaFast > emaSlow;
        bool emaBearCross = emaFast < emaSlow;

        // ── VWAP confirmation (from foundation tier) ──
        bool priceAboveVwap = foundation.Vwap > 0 && signal.Price > foundation.Vwap;

        // ── Volume expansion ──
        bool volumeExpanding = IsVolumeExpanding(candles);

        // ── Confluence scoring ──
        bool isBullish = rsiBullish;
        int confirmedFactors = 1; // RSI is the base factor

        if (hasTrend) confirmedFactors++;
        if (mtfConfirmed) confirmedFactors++;
        if (isBullish ? emaBullCross : emaBearCross) confirmedFactors++;

        // Need RSI + at least 2 other factors for a signal (3/4 minimum)
        if (confirmedFactors < 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // Build confidence
        float confidence = confirmedFactors * _opts.BaseConfidencePerFactor;
        if (volumeExpanding) confidence += _opts.VolumeExpansionBonus;
        if (strongTrend) confidence += _opts.StrongTrendBonus;
        if (isBullish == priceAboveVwap && foundation.Vwap > 0) confidence += _opts.VwapAgreementBonus;
        else if (foundation.Vwap > 0 && isBullish != priceAboveVwap) confidence -= _opts.VwapDisagreementPenalty;

        var reasons = new List<string>(6)
        {
            $"RSI({_opts.RsiPeriod})={rsi:F1}",
            $"ADX={adx:F1}{(hasTrend ? strongTrend ? " strong" : " trending" : " weak")}",
            $"MTF-RSI {(mtfConfirmed ? "confirmed" : "divergent")}",
            $"EMA {_opts.EmaFastPeriod}/{_opts.EmaSlowPeriod} {(emaBullCross ? "bull" : "bear")} cross"
        };
        if (volumeExpanding) reasons.Add("vol expanding");
        if (foundation.Vwap > 0) reasons.Add(priceAboveVwap ? "above VWAP" : "below VWAP");

        var reasoning = string.Join(" | ", reasons);

        if (isBullish)
        {
            return Task.FromResult(StrategyVote.Approve(StrategyTag, confidence,
                $"Bullish momentum ({confirmedFactors}/4 factors): {reasoning}", priorWeight: SwarmWeight));
        }
        else
        {
            return Task.FromResult(StrategyVote.Reject(StrategyTag, confidence,
                $"Bearish momentum ({confirmedFactors}/4 factors): {reasoning}", priorWeight: SwarmWeight));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Technical Indicators
    // ═══════════════════════════════════════════════════════════════════════

    internal static double ComputeRsi(IReadOnlyList<Candle> candles, int period)
    {
        var closes = candles.TakeLast(period + 1).Select(c => c.Close).ToList();
        double gains = 0, losses = 0;
        for (int i = 1; i < closes.Count; i++)
        {
            var diff = (double)(closes[i] - closes[i - 1]);
            if (diff > 0) gains += diff;
            else losses -= diff;
        }
        if (losses == 0) return gains == 0 ? 50.0 : 100.0;
        var rs = gains / losses;
        return 100.0 - (100.0 / (1 + rs));
    }

    /// <summary>
    /// Average Directional Index (ADX) — measures trend strength regardless of direction.
    /// Uses Wilder's smoothing (1/N exponential).
    /// Returns 0-100. Below 20 = no trend, 20-25 = emerging, 25+ = strong trend.
    /// </summary>
    internal static double ComputeAdx(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period * 2 + 1) return 0;

        var data = candles.TakeLast(period * 2 + 1).ToList();

        double smoothPlusDm = 0, smoothMinusDm = 0, smoothTr = 0;
        double adxAccum = 0;
        int adxCount = 0;

        for (int i = 1; i < data.Count; i++)
        {
            var high = (double)data[i].High;
            var low = (double)data[i].Low;
            var prevHigh = (double)data[i - 1].High;
            var prevLow = (double)data[i - 1].Low;
            var prevClose = (double)data[i - 1].Close;

            // True Range
            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));

            // Directional Movement
            var plusDm = (high - prevHigh > prevLow - low) && (high - prevHigh > 0) ? high - prevHigh : 0;
            var minusDm = (prevLow - low > high - prevHigh) && (prevLow - low > 0) ? prevLow - low : 0;

            if (i <= period)
            {
                // Initial accumulation
                smoothPlusDm += plusDm;
                smoothMinusDm += minusDm;
                smoothTr += tr;
            }
            else
            {
                // Wilder's smoothing: smooth = prev - (prev / period) + current
                smoothPlusDm = smoothPlusDm - (smoothPlusDm / period) + plusDm;
                smoothMinusDm = smoothMinusDm - (smoothMinusDm / period) + minusDm;
                smoothTr = smoothTr - (smoothTr / period) + tr;

                if (smoothTr > 0)
                {
                    var plusDi = 100.0 * smoothPlusDm / smoothTr;
                    var minusDi = 100.0 * smoothMinusDm / smoothTr;
                    var diSum = plusDi + minusDi;
                    var dx = diSum > 0 ? 100.0 * Math.Abs(plusDi - minusDi) / diSum : 0;

                    adxAccum += dx;
                    adxCount++;
                }
            }
        }

        return adxCount > 0 ? adxAccum / adxCount : 0;
    }

    /// <summary>
    /// Exponential Moving Average of closing prices.
    /// </summary>
    internal static decimal ComputeEma(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period) return candles[^1].Close;

        var data = candles.TakeLast(Math.Max(period * 2, candles.Count)).ToList();
        decimal multiplier = 2m / (period + 1);

        // Seed with SMA of first 'period' bars
        decimal ema = 0;
        for (int i = 0; i < period && i < data.Count; i++)
            ema += data[i].Close;
        ema /= period;

        // Apply EMA from bar 'period' onward
        for (int i = period; i < data.Count; i++)
            ema = (data[i].Close - ema) * multiplier + ema;

        return ema;
    }

    /// <summary>
    /// Aggregates candles into higher-timeframe bars (e.g., 4 x 15m = 1h).
    /// </summary>
    internal static List<Candle> AggregateCandles(IReadOnlyList<Candle> candles, int factor)
    {
        var result = new List<Candle>(candles.Count / factor);
        for (int i = 0; i + factor - 1 < candles.Count; i += factor)
        {
            var slice = candles.Skip(i).Take(factor).ToList();
            result.Add(new Candle
            {
                Timestamp = slice[0].Timestamp,
                Open = slice[0].Open,
                High = slice.Max(c => c.High),
                Low = slice.Min(c => c.Low),
                Close = slice[^1].Close,
                Volume = slice.Sum(c => c.Volume)
            });
        }
        return result;
    }

    private bool IsVolumeExpanding(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 8) return false;
        var last3Avg = candles.TakeLast(3).Average(c => c.Volume);
        var prev5Avg = candles.TakeLast(8).Take(5).Average(c => c.Volume);
        return prev5Avg > 0 && last3Avg > prev5Avg * (decimal)_opts.VolumeExpansionMultiplier;
    }
}
