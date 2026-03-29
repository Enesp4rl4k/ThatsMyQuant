using System.Buffers;
using System.Runtime.InteropServices;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Infrastructure.Indicators;

/// <summary>
/// High-performance C# port of the AlphaTrend indicator.
/// Optimized for zero-allocation hotspots using Spans and ArrayPool.
/// </summary>
public static class AlphaTrendCalculator
{
    public static void Calculate(
        ReadOnlySpan<Candle> candles,
        Span<AlphaTrendResult> results,
        double coeff = 1.0,
        int period = 14)
    {
        int count = candles.Length;
        if (count == 0) return;
        if (results.Length < count) 
            throw new ArgumentException("Results span too small.");

        if (count < period + 3)
        {
            for (int i = 0; i < count; i++) results[i] = new AlphaTrendResult();
            return;
        }

        var pool = ArrayPool<double>.Shared;
        double[] atr = pool.Rent(count);
        double[] rsi = pool.Rent(count);
        double[] alphaTrend = pool.Rent(count);

        try
        {
            ComputeATR(candles, atr.AsSpan(0, count), period);
            ComputeRSI(candles, rsi.AsSpan(0, count), period);

            var state = new SignalState();

            for (int i = 0; i < count; i++)
            {
                if (i < period)
                {
                    alphaTrend[i] = (double)candles[i].Close;
                    results[i] = new AlphaTrendResult { Value = alphaTrend[i] };
                    continue;
                }

                alphaTrend[i] = CalculateAlphaTrendValue(candles[i], alphaTrend[i - 1], atr[i], rsi[i], coeff);

                if (i < 3)
                {
                    results[i] = new AlphaTrendResult { Value = alphaTrend[i] };
                    continue;
                }

                results[i] = GenerateResultStep(alphaTrend, i, state);
            }
        }
        finally
        {
            pool.Return(atr);
            pool.Return(rsi);
            pool.Return(alphaTrend);
        }
    }

    private static double CalculateAlphaTrendValue(Candle candle, double prevAt, double atr, double rsi, double coeff)
    {
        double upT = (double)candle.Low - atr * coeff;
        double downT = (double)candle.High + atr * coeff;
        return (rsi >= 50) ? (upT < prevAt ? prevAt : upT) : (downT > prevAt ? prevAt : downT);
    }

    private static AlphaTrendResult GenerateResultStep(double[] alphaTrend, int i, SignalState state)
    {
        double at = alphaTrend[i];
        double at1 = alphaTrend[i - 1];
        double at2 = alphaTrend[i - 2];
        double at3 = alphaTrend[i - 3];

        bool buySignal = at > at2 && at1 <= at3;
        bool sellSignal = at < at2 && at1 >= at3;

        TradeSide? signal = null;
        bool confirmed = false;

        if (buySignal && (i - state.LastBuyBar > i - state.LastSellBar))
        {
            signal = TradeSide.Buy;
            confirmed = true;
            state.LastBuyBar = i;
        }
        else if (sellSignal && (i - state.LastSellBar > i - state.LastBuyBar))
        {
            signal = TradeSide.Sell;
            confirmed = true;
            state.LastSellBar = i;
        }

        return new AlphaTrendResult
        {
            Value = at,
            PreviousValue = at2,
            Signal = signal,
            IsConfirmed = confirmed,
            IsBullish = at > at2
        };
    }

    private class SignalState
    {
        public int LastBuyBar { get; set; } = -1000;
        public int LastSellBar { get; set; } = -1000;
    }

    private static void ComputeATR(ReadOnlySpan<Candle> candles, Span<double> atr, int period)
    {
        double trSum = 0;
        for (int i = 0; i < candles.Length; i++)
        {
            double tr = CalculateTrueRange(candles, i);
            trSum += tr;
            if (i >= period) trSum -= CalculateTrueRange(candles, i - period);
            
            if (i >= period - 1) atr[i] = trSum / period;
        }
    }

    private static double CalculateTrueRange(ReadOnlySpan<Candle> candles, int i)
    {
        if (i <= 0) return (double)(candles[0].High - candles[0].Low);
        return Math.Max((double)(candles[i].High - candles[i].Low), 
               Math.Max(Math.Abs((double)(candles[i].High - candles[i - 1].Close)), 
                        Math.Abs((double)(candles[i].Low - candles[i - 1].Close))));
    }

    private static void ComputeRSI(ReadOnlySpan<Candle> candles, Span<double> rsi, int period)
    {
        double avgGain = 0, avgLoss = 0;
        for (int i = 1; i < candles.Length; i++)
        {
            double diff = (double)(candles[i].Close - candles[i - 1].Close);
            double gain = diff > 0 ? diff : 0;
            double loss = diff < 0 ? -diff : 0;

            if (i <= period)
            {
                avgGain += gain;
                avgLoss += loss;
                if (i == period)
                {
                    avgGain /= period;
                    avgLoss /= period;
                }
            }
            else
            {
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }

            if (i >= period)
                rsi[i] = avgLoss < 1e-10 ? 100 : 100 - (100 / (1 + avgGain / avgLoss));
            else
                rsi[i] = 50;
        }
    }
}
