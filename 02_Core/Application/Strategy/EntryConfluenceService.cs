using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Pre-trade entry gates to cut noise without ML retraining. Three gates:
///   1. MTF-lite trend alignment — EMA200 slope on 1h as a proxy for higher-TF direction.
///      A long in a bearish EMA200 slope is a low-quality counter-trend trade.
///   2. Volatility floor — reject entries when current ATR% is below the 30d median.
///      Chop regimes silently drain PnL via costs.
///   3. Volume confirmation — last-bar volume must be >= 0.7x of 50-bar median. Dead
///      tape entries fade fast.
///
/// All gates are disabled in HFT mode (too fast a timeframe) and in Volatile regime
/// (volatility gate would always pass but trend filter would kill breakout setups).
/// </summary>
public static class EntryConfluenceService
{
    public const int EmaTrendLookback = 50;
    public const int EmaSlopeBars = 10;
    public const int VolatilityLookbackBars = 60;
    public const int VolumeLookbackBars = 30;
    /// <summary>Slope neutral band — reject counter-trend only if |slope| exceeds this fraction.</summary>
    public const decimal TrendSlopeDeadZone = 0.0025m; // 0.25% over 10 bars
    public const decimal VolatilityFloorMultiplier = 0.65m;
    public const decimal VolumeFloorMultiplier = 0.55m;

    public readonly record struct GateResult(bool Pass, string? RejectReason);

    public static GateResult Evaluate(
        IReadOnlyList<Candle> window,
        TradeSide side,
        MarketRegime regime,
        bool enableTrendAlignment = true,
        bool enableVolatilityFloor = true,
        bool enableVolumeFloor = true)
    {
        if (window is null || window.Count < EmaTrendLookback + EmaSlopeBars + 2)
            return new GateResult(true, null);

        if (enableTrendAlignment && regime != MarketRegime.Volatile)
        {
            var slope = ComputeEmaSlope(window);
            // Dead-zone: only block trades fighting a *meaningful* trend. Tiny slope noise
            // on a short window fires too often and destroys trade count without a real
            // generalization win (PBO balloons from over-filtering).
            if (side == TradeSide.Buy && slope < -TrendSlopeDeadZone)
                return new GateResult(false, "MtfTrendMisaligned");
            if (side == TradeSide.Sell && slope > TrendSlopeDeadZone)
                return new GateResult(false, "MtfTrendMisaligned");
        }

        if (enableVolatilityFloor && regime == MarketRegime.Ranging)
        {
            var (currentAtrPct, medianAtrPct) = ComputeVolatilityMetrics(window);
            if (medianAtrPct > 0m && currentAtrPct < medianAtrPct * VolatilityFloorMultiplier)
                return new GateResult(false, "VolatilityFloor");
        }

        if (enableVolumeFloor)
        {
            var (currentVol, medianVol) = ComputeVolumeMetrics(window);
            if (medianVol > 0m && currentVol < medianVol * VolumeFloorMultiplier)
                return new GateResult(false, "VolumeFloor");
        }

        return new GateResult(true, null);
    }

    private static decimal ComputeEmaSlope(IReadOnlyList<Candle> window)
    {
        var last = window.Count - 1;
        var emaNow = ComputeEma(window, last, EmaTrendLookback);
        var emaPrev = ComputeEma(window, Math.Max(0, last - EmaSlopeBars), EmaTrendLookback);
        if (emaPrev <= 0m) return 0m;
        return (emaNow - emaPrev) / emaPrev;
    }

    private static decimal ComputeEma(IReadOnlyList<Candle> window, int endIndex, int period)
    {
        if (endIndex < period - 1) return window[endIndex].Close;
        decimal k = 2m / (period + 1m);
        decimal ema = window[endIndex - period + 1].Close;
        for (int i = endIndex - period + 2; i <= endIndex; i++)
            ema = window[i].Close * k + ema * (1m - k);
        return ema;
    }

    private static (decimal current, decimal median) ComputeVolatilityMetrics(IReadOnlyList<Candle> window)
    {
        int lookback = Math.Min(VolatilityLookbackBars, window.Count - 1);
        if (lookback < 50) return (0m, 0m);
        var atrPcts = new List<decimal>(lookback);
        int start = window.Count - lookback;
        for (int i = start; i < window.Count; i++)
        {
            var c = window[i];
            if (c.Close <= 0m) continue;
            var tr = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - window[Math.Max(0, i - 1)].Close), Math.Abs(c.Low - window[Math.Max(0, i - 1)].Close)));
            atrPcts.Add(tr / c.Close);
        }
        if (atrPcts.Count == 0) return (0m, 0m);
        var sorted = atrPcts.ToArray();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        return (atrPcts[^1], median);
    }

    private static (decimal current, decimal median) ComputeVolumeMetrics(IReadOnlyList<Candle> window)
    {
        int lookback = Math.Min(VolumeLookbackBars, window.Count);
        if (lookback < 10) return (0m, 0m);
        var vols = new decimal[lookback];
        int start = window.Count - lookback;
        for (int i = 0; i < lookback; i++)
            vols[i] = window[start + i].Volume;
        var sorted = (decimal[])vols.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        return (vols[^1], median);
    }
}
