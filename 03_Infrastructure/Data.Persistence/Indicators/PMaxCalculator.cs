using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Infrastructure.Indicators;

/// <summary>
/// Lightweight C# port of the PMax indicator using EMA and ATR.
/// </summary>
public static class PMaxCalculator
{
    public static List<PMaxResult> Calculate(
        List<Candle> candles,
        int atrLength = 10,
        double atrMultiplier = 3.0,
        int maLength = 10)
    {
        var results = new List<PMaxResult>();
        if (candles.Count == 0) return results;

        var hl2 = candles.Select(c => (double)((c.High + c.Low) / 2m)).ToList();
        var ema = ComputeEma(hl2, maLength);
        var atr = ComputeAtr(candles, atrLength);

        var longStop = new double[candles.Count];
        var shortStop = new double[candles.Count];
        var dir = new int[candles.Count];
        var pmax = new double[candles.Count];

        for (int i = 0; i < candles.Count; i++)
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

            results.Add(new PMaxResult
            {
                MovingAverage = ema[i],
                Value = pmax[i],
                Signal = signal,
                IsBullish = ema[i] >= pmax[i]
            });
        }

        return results;
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

    private static double[] ComputeEma(List<double> values, int length)
    {
        var result = new double[values.Count];
        if (values.Count == 0) return result;

        var alpha = 2.0 / (length + 1);
        result[0] = values[0];

        for (int i = 1; i < values.Count; i++)
        {
            result[i] = (alpha * values[i]) + ((1 - alpha) * result[i - 1]);
        }
        return result;
    }

    private static double[] ComputeAtr(List<Candle> candles, int length)
    {
        var tr = new double[candles.Count];
        var atr = new double[candles.Count];

        for (int i = 0; i < candles.Count; i++)
        {
            tr[i] = CalculateTrueRange(candles, i);
            atr[i] = i == 0 ? tr[i] : ((atr[i - 1] * (length - 1)) + tr[i]) / length;
        }
        return atr;
    }

    private static double CalculateTrueRange(List<Candle> candles, int i)
    {
        if (i == 0) return (double)(candles[i].High - candles[i].Low);

        var hl = (double)(candles[i].High - candles[i].Low);
        var hc = Math.Abs((double)(candles[i].High - candles[i - 1].Close));
        var lc = Math.Abs((double)(candles[i].Low - candles[i - 1].Close));
        
        return Math.Max(hl, Math.Max(hc, lc));
    }
}
