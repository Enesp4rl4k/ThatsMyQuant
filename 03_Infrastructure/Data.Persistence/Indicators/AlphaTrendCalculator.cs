using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Infrastructure.Indicators;

public static class AlphaTrendCalculator
{
    public static List<AlphaTrendResult> Calculate(List<Candle> candles, double coeff = 1.0, int period = 14)
    {
        int count = candles.Count;
        var results = new List<AlphaTrendResult>(count);

        if (count < period + 3)
        {
            for (int i = 0; i < count; i++) results.Add(new AlphaTrendResult());
            return results;
        }

        // Step 1 & 2: O(n) ATR Calculation (Rolling Window)
        var atr = ComputeATR(candles, period);
        var rsi = ComputeRSI(candles, period);

        // Step 3: AlphaTrend calculation
        var alphaTrend = new double[count];
        int lastBuyBar = -1000;
        int lastSellBar = -1000;

        for (int i = 0; i < count; i++)
        {
            if (i < period)
            {
                alphaTrend[i] = (double)candles[i].Close;
                results.Add(new AlphaTrendResult { Value = alphaTrend[i] });
                continue;
            }

            double upT = (double)candles[i].Low - atr[i] * coeff;
            double downT = (double)candles[i].High + atr[i] * coeff;
            double prev = alphaTrend[i - 1];

            // Kivanc's core logic
            alphaTrend[i] = (rsi[i] >= 50) 
                ? (upT < prev ? prev : upT) 
                : (downT > prev ? prev : downT);

            // Signal Generation (minimum index check)
            if (i < 3) continue;

            double at = alphaTrend[i];
            double at2 = alphaTrend[i - 2];
            double at1 = alphaTrend[i - 1];
            double at3 = alphaTrend[i - 3];

            bool buySignal = at > at2 && at1 <= at3;
            bool sellSignal = at < at2 && at1 >= at3;

            TradeSide? signal = null;
            bool confirmed = false;

            if (buySignal && (i - lastBuyBar > i - lastSellBar))
            {
                signal = TradeSide.Buy;
                confirmed = true;
                lastBuyBar = i;
            }
            else if (sellSignal && (i - lastSellBar > i - lastBuyBar))
            {
                signal = TradeSide.Sell;
                confirmed = true;
                lastSellBar = i;
            }

            results.Add(new AlphaTrendResult
            {
                Value = at,
                PreviousValue = at2,
                Signal = signal,
                IsConfirmed = confirmed,
                IsBullish = at > at2
            });
        }

        return results;
    }

    private static double[] ComputeATR(List<Candle> candles, int period)
    {
        int count = candles.Count;
        var atr = new double[count];
        double trSum = 0;

        for (int i = 0; i < count; i++)
        {
            double tr = (i == 0) 
                ? (double)(candles[i].High - candles[i].Low)
                : Math.Max((double)(candles[i].High - candles[i].Low), 
                  Math.Max(Math.Abs((double)(candles[i].High - candles[i - 1].Close)), 
                           Math.Abs((double)(candles[i].Low - candles[i - 1].Close))));

            trSum += tr;
            if (i >= period) trSum -= GetTR(candles, i - period); // Rolling Sum
            
            if (i >= period - 1) atr[i] = trSum / period;
        }
        return atr;
    }

    private static double GetTR(List<Candle> candles, int i)
    {
        if (i <= 0) return (double)(candles[0].High - candles[0].Low);
        return Math.Max((double)(candles[i].High - candles[i].Low), 
               Math.Max(Math.Abs((double)(candles[i].High - candles[i - 1].Close)), 
                        Math.Abs((double)(candles[i].Low - candles[i - 1].Close))));
    }

    private static double[] ComputeRSI(List<Candle> candles, int period)
    {
        int count = candles.Count;
        var rsi = new double[count];
        double avgGain = 0, avgLoss = 0;

        for (int i = 1; i < count; i++)
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
                // Wilder's Smoothing
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }

            if (i >= period)
                rsi[i] = avgLoss < 1e-10 ? 100 : 100 - (100 / (1 + avgGain / avgLoss));
            else
                rsi[i] = 50;
        }
        return rsi;
    }
}