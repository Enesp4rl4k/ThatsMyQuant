using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

public sealed class MomentumStrategyAgent : IStrategyAgent
{
    private readonly ILogger<MomentumStrategyAgent> _logger;
    private readonly MomentumOptions _options;
    private readonly StrategyTuningService? _tuningService;

    public string StrategyTag => "Momentum";
    public int SwarmWeight => ResolveOptions().SwarmWeight;

    public MomentumStrategyAgent(
        ILogger<MomentumStrategyAgent> logger,
        IOptions<MomentumOptions>? options = null,
        StrategyTuningService? tuningService = null)
    {
        _logger = logger;
        _options = options?.Value ?? new MomentumOptions();
        _tuningService = tuningService;
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
        var options = ResolveOptions();

        if (candles.Count < options.MinCandles)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var rsi = ComputeRsi(candles, options.RsiPeriod);
        var isBullish = rsi > options.RsiBullThreshold;
        var isBearish = rsi < options.RsiBearThreshold;

        if (!isBullish && !isBearish)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var adx = ComputeAdx(candles, options.AdxPeriod);
        var hasTrend = adx >= options.AdxTrendThreshold;
        var strongTrend = adx >= options.AdxStrongThreshold;

        var aggregated = AggregateCandles(candles, options.MultiTfAggFactor);
        var mtfConfirmed = false;
        if (aggregated.Count >= options.RsiPeriod + 1)
        {
            var mtfRsi = ComputeRsi(aggregated, options.RsiPeriod);
            mtfConfirmed = (isBullish && mtfRsi > options.MultiTfRsiMidpoint)
                || (isBearish && mtfRsi < options.MultiTfRsiMidpoint);
        }

        var emaFast = ComputeEma(candles, options.EmaFastPeriod);
        var emaSlow = ComputeEma(candles, options.EmaSlowPeriod);
        var emaConfirmed = isBullish ? emaFast > emaSlow : emaFast < emaSlow;

        var priceAboveVwap = foundation.Vwap > 0 && signal.Price > foundation.Vwap;
        var volumeExpanding = IsVolumeExpanding(candles);

        var confirmedFactors = 1;
        if (hasTrend) confirmedFactors++;
        if (mtfConfirmed) confirmedFactors++;
        if (emaConfirmed) confirmedFactors++;

        if (confirmedFactors < 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        float confidence = confirmedFactors * options.BaseConfidencePerFactor;
        if (volumeExpanding) confidence += options.VolumeExpansionBonus;
        if (strongTrend) confidence += options.StrongTrendBonus;

        if (foundation.Vwap > 0)
        {
            var vwapAgreement = isBullish == priceAboveVwap;
            confidence += vwapAgreement ? options.VwapAgreementBonus : -options.VwapDisagreementPenalty;
        }

        var reasons = new List<string>
        {
            $"RSI({options.RsiPeriod})={rsi:F1}",
            $"ADX={adx:F1}{(hasTrend ? strongTrend ? " strong" : " trending" : " weak")}",
            $"MTF-RSI {(mtfConfirmed ? "confirmed" : "divergent")}",
            $"EMA {options.EmaFastPeriod}/{options.EmaSlowPeriod} {(emaConfirmed ? "aligned" : "divergent")}"
        };

        if (volumeExpanding) reasons.Add("vol expanding");
        if (foundation.Vwap > 0) reasons.Add(priceAboveVwap ? "above VWAP" : "below VWAP");

        var reasoning = string.Join(" | ", reasons);
        return Task.FromResult(isBullish
            ? StrategyVote.Approve(StrategyTag, confidence, $"Bullish momentum ({confirmedFactors}/4 factors): {reasoning}")
            : StrategyVote.Reject(StrategyTag, confidence, $"Bearish momentum ({confirmedFactors}/4 factors): {reasoning}"));
    }

    private static double ComputeRsi(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period + 1) return 50.0;

        var closes = candles.TakeLast(period + 1).Select(c => c.Close).ToList();
        double gains = 0;
        double losses = 0;

        for (var i = 1; i < closes.Count; i++)
        {
            var diff = (double)(closes[i] - closes[i - 1]);
            if (diff > 0) gains += diff;
            else losses -= diff;
        }

        if (losses == 0) return gains == 0 ? 50.0 : 100.0;

        var rs = gains / losses;
        return 100.0 - (100.0 / (1.0 + rs));
    }

    private static double ComputeAdx(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period * 2 + 1) return 0;

        var data = candles.TakeLast(period * 2 + 1).ToList();
        double smoothPlusDm = 0;
        double smoothMinusDm = 0;
        double smoothTr = 0;
        double adxAccum = 0;
        var adxCount = 0;

        for (var i = 1; i < data.Count; i++)
        {
            var high = (double)data[i].High;
            var low = (double)data[i].Low;
            var prevHigh = (double)data[i - 1].High;
            var prevLow = (double)data[i - 1].Low;
            var prevClose = (double)data[i - 1].Close;

            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            var plusDm = (high - prevHigh > prevLow - low) && (high - prevHigh > 0) ? high - prevHigh : 0;
            var minusDm = (prevLow - low > high - prevHigh) && (prevLow - low > 0) ? prevLow - low : 0;

            if (i <= period)
            {
                smoothPlusDm += plusDm;
                smoothMinusDm += minusDm;
                smoothTr += tr;
                continue;
            }

            smoothPlusDm = smoothPlusDm - (smoothPlusDm / period) + plusDm;
            smoothMinusDm = smoothMinusDm - (smoothMinusDm / period) + minusDm;
            smoothTr = smoothTr - (smoothTr / period) + tr;

            if (smoothTr <= 0) continue;

            var plusDi = 100.0 * smoothPlusDm / smoothTr;
            var minusDi = 100.0 * smoothMinusDm / smoothTr;
            var diSum = plusDi + minusDi;
            var dx = diSum > 0 ? 100.0 * Math.Abs(plusDi - minusDi) / diSum : 0;
            adxAccum += dx;
            adxCount++;
        }

        return adxCount > 0 ? adxAccum / adxCount : 0;
    }

    private static decimal ComputeEma(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period) return candles[^1].Close;

        var data = candles.TakeLast(Math.Max(period * 2, period + 1)).ToList();
        decimal ema = data.Take(period).Average(c => c.Close);
        var multiplier = 2m / (period + 1);

        for (var i = period; i < data.Count; i++)
            ema = (data[i].Close - ema) * multiplier + ema;

        return ema;
    }

    private static List<Candle> AggregateCandles(IReadOnlyList<Candle> candles, int factor)
    {
        var result = new List<Candle>(candles.Count / Math.Max(1, factor));
        for (var i = 0; i + factor - 1 < candles.Count; i += factor)
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

        var recent = candles.TakeLast(3).Average(c => c.Volume);
        var previous = candles.TakeLast(8).Take(5).Average(c => c.Volume);
        return previous > 0 && recent > previous * (decimal)ResolveOptions().VolumeExpansionMultiplier;
    }

    private MomentumOptions ResolveOptions()
    {
        return _tuningService?.GetMomentumOptions() ?? _options;
    }
}
