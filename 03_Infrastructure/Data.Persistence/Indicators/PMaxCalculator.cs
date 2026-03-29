using System.Buffers;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Infrastructure.Indicators;

/// <summary>
/// High-performance C# port of the PMax indicator.
/// Optimized for zero-allocation hotspots using Spans and ArrayPool.
/// </summary>
public static class PMaxCalculator
{
    public static void Calculate(
        ReadOnlySpan<Candle> candles,
        Span<PMaxResult> results,
        int atrLength = 10,
        double atrMultiplier = 3.0,
        int maLength = 10)
    {
        if (candles.Length == 0) return;
        if (results.Length < candles.Length) 
            throw new ArgumentException("Results span must be at least as long as candles span.");

        int count = candles.Length;
        var pool = ArrayPool<double>.Shared;
        
        double[] hl2 = pool.Rent(count);
        double[] ema = pool.Rent(count);
        double[] atr = pool.Rent(count);
        double[] longStop = pool.Rent(count);
        double[] shortStop = pool.Rent(count);
        int[] dir = ArrayPool<int>.Shared.Rent(count);
        double[] pmax = pool.Rent(count);

        try
        {
            for (int i = 0; i < count; i++)
                hl2[i] = (double)((candles[i].High + candles[i].Low) / 2m);

            ComputeEma(hl2.AsSpan(0, count), ema.AsSpan(0, count), maLength);
            ComputeAtr(candles, atr.AsSpan(0, count), atrLength);

            for (int i = 0; i < count; i++)
            {
                UpdateStops(i, ema[i], atr[i], atrMultiplier, longStop, shortStop);
                
                if (i > 0)
                {
                    ApplyTrailingLogic(i, ema[i], longStop, shortStop, dir);
                }
                else
                {
                    dir[i] = 1;
                }

                pmax[i] = dir[i] == 1 ? longStop[i] : shortStop[i];
                TradeSide? signal = DetectSignal(i, ema, pmax);

                results[i] = new PMaxResult
                {
                    MovingAverage = ema[i],
                    Value = pmax[i],
                    Signal = signal,
                    IsBullish = ema[i] >= pmax[i]
                };
            }
        }
        finally
        {
            pool.Return(hl2);
            pool.Return(ema);
            pool.Return(atr);
            pool.Return(longStop);
            pool.Return(shortStop);
            ArrayPool<int>.Shared.Return(dir);
            pool.Return(pmax);
        }
    }

    private static void UpdateStops(int i, double ma, double atrValue, double atrMultiplier, double[] longStop, double[] shortStop)
    {
        longStop[i] = ma - (atrMultiplier * atrValue);
        shortStop[i] = ma + (atrMultiplier * atrValue);
    }

    private static void ApplyTrailingLogic(int i, double ma, double[] longStop, double[] shortStop, int[] dir)
    {
        var prevLongStop = longStop[i - 1];
        var prevShortStop = shortStop[i - 1];

        longStop[i] = ma > prevLongStop ? Math.Max(longStop[i], prevLongStop) : longStop[i];
        shortStop[i] = ma < prevShortStop ? Math.Min(shortStop[i], prevShortStop) : shortStop[i];

        dir[i] = dir[i - 1];
        if (dir[i - 1] == -1 && ma > prevShortStop)
        {
            dir[i] = 1;
        }
        else if (dir[i - 1] == 1 && ma < prevLongStop)
        {
            dir[i] = -1;
        }
    }

    private static TradeSide? DetectSignal(int i, double[] ema, double[] pmax)
    {
        if (i <= 0) return null;

        if (ema[i] > pmax[i] && ema[i - 1] <= pmax[i - 1])
            return TradeSide.Buy;
        
        if (ema[i] < pmax[i] && ema[i - 1] >= pmax[i - 1])
            return TradeSide.Sell;

        return null;
    }

    private static void ComputeEma(ReadOnlySpan<double> values, Span<double> result, int length)
    {
        if (values.Length == 0) return;

        var alpha = 2.0 / (length + 1);
        result[0] = values[0];

        for (int i = 1; i < values.Length; i++)
        {
            result[i] = (alpha * values[i]) + ((1 - alpha) * result[i - 1]);
        }
    }

    private static void ComputeAtr(ReadOnlySpan<Candle> candles, Span<double> atr, int length)
    {
        double prevAtr = 0;
        for (int i = 0; i < candles.Length; i++)
        {
            double tr = CalculateTrueRange(candles, i);
            atr[i] = i == 0 ? tr : ((prevAtr * (length - 1)) + tr) / length;
            prevAtr = atr[i];
        }
    }

    private static double CalculateTrueRange(ReadOnlySpan<Candle> candles, int i)
    {
        if (i == 0) return (double)(candles[i].High - candles[i].Low);

        var hl = (double)(candles[i].High - candles[i].Low);
        var hc = Math.Abs((double)(candles[i].High - candles[i - 1].Close));
        var lc = Math.Abs((double)(candles[i].Low - candles[i - 1].Close));
        
        return Math.Max(hl, Math.Max(hc, lc));
    }
}
