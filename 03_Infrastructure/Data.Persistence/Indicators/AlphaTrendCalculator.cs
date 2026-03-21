using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Infrastructure.Indicators;

/// <summary>
/// C# port of KivancOzbilgic's AlphaTrend indicator (Pine Script v5).
///
/// Logic:
///   ATR = SMA(TrueRange, period)
///   upT = Low - ATR * coeff
///   downT = High + ATR * coeff
///   AlphaTrend = RSI >= 50 ? max(upT, prev) : min(downT, prev)
///   Buy = crossover(AT, AT[2])  with barsSince confirmation
///   Sell = crossunder(AT, AT[2]) with barsSince confirmation
/// </summary>
public static class AlphaTrendCalculator
{
    public static List<AlphaTrendResult> Calculate(List<Candle> candles, double coeff = 1.0, int period = 14)
    {
        var results = new List<AlphaTrendResult>();
        int count = candles.Count;

        if (count < period + 3)
        {
            // Not enough data — return empty results
            for (int i = 0; i < count; i++)
                results.Add(new AlphaTrendResult());
            return results;
        }

        // Step 1: Compute True Range
        var tr = new double[count];
        tr[0] = (double)(candles[0].High - candles[0].Low);
        for (int i = 1; i < count; i++)
        {
            double hl = (double)(candles[i].High - candles[i].Low);
            double hc = Math.Abs((double)(candles[i].High - candles[i - 1].Close));
            double lc = Math.Abs((double)(candles[i].Low - candles[i - 1].Close));
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }

        // Step 2: ATR = SMA(TR, period)
        var atr = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (i < period - 1)
            {
                atr[i] = 0;
                continue;
            }
            double sum = 0;
            for (int j = i - period + 1; j <= i; j++)
                sum += tr[j];
            atr[i] = sum / period;
        }

        // Step 3: RSI(close, period)
        var rsi = ComputeRSI(candles, period);

        // Step 4: AlphaTrend calculation
        var alphaTrend = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (i < period)
            {
                alphaTrend[i] = (double)candles[i].Close;
                continue;
            }

            double upT = (double)candles[i].Low - atr[i] * coeff;
            double downT = (double)candles[i].High + atr[i] * coeff;

            bool bullish = rsi[i] >= 50;
            double prev = alphaTrend[i - 1];

            if (bullish)
                alphaTrend[i] = upT < prev ? prev : upT;
            else
                alphaTrend[i] = downT > prev ? prev : downT;
        }

        // Step 5: Generate signals with barsSince confirmation
        int lastBuyBar = -1000;
        int lastSellBar = -1000;

        for (int i = 0; i < count; i++)
        {
            if (i < 3)
            {
                results.Add(new AlphaTrendResult
                {
                    Value = alphaTrend[i],
                    PreviousValue = alphaTrend[i],
                    IsBullish = true
                });
                continue;
            }

            double at = alphaTrend[i];
            double at2 = alphaTrend[i - 2]; // AT[2] — 2 bars back

            // Crossover: AT crosses above AT[2]
            bool buySignal = alphaTrend[i] > alphaTrend[i - 2] && alphaTrend[i - 1] <= alphaTrend[i - 3];
            // Crossunder: AT crosses below AT[2]
            bool sellSignal = alphaTrend[i] < alphaTrend[i - 2] && alphaTrend[i - 1] >= alphaTrend[i - 3];

            // barsSince confirmation: prevent duplicate signals
            // O1 > K2: barsSince(prevBuy) > barsSince(lastSell)
            bool buyConfirmed = false;
            bool sellConfirmed = false;

            if (buySignal)
            {
                int barsSinceLastSell = i - lastSellBar;
                int barsSincePrevBuy = i - lastBuyBar;
                buyConfirmed = barsSincePrevBuy > barsSinceLastSell;
                if (buyConfirmed) lastBuyBar = i;
            }

            if (sellSignal)
            {
                int barsSinceLastBuy = i - lastBuyBar;
                int barsSincePrevSell = i - lastSellBar;
                sellConfirmed = barsSincePrevSell > barsSinceLastBuy;
                if (sellConfirmed) lastSellBar = i;
            }

            TradeSide? signal = null;
            bool confirmed = false;
            if (buySignal) { signal = TradeSide.Buy; confirmed = buyConfirmed; }
            else if (sellSignal) { signal = TradeSide.Sell; confirmed = sellConfirmed; }

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

    private static double[] ComputeRSI(List<Candle> candles, int period)
    {
        int count = candles.Count;
        var rsi = new double[count];
        var gains = new double[count];
        var losses = new double[count];

        for (int i = 1; i < count; i++)
        {
            double change = (double)(candles[i].Close - candles[i - 1].Close);
            gains[i] = change > 0 ? change : 0;
            losses[i] = change < 0 ? -change : 0;
        }

        // SMA for first period
        double avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= period && i < count; i++)
        {
            avgGain += gains[i];
            avgLoss += losses[i];
        }
        avgGain /= period;
        avgLoss /= period;

        for (int i = 0; i < count; i++)
        {
            if (i < period)
            {
                rsi[i] = 50; // Default
                continue;
            }

            if (i == period)
            {
                rsi[i] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
                continue;
            }

            // Wilder's smoothed RSI
            avgGain = (avgGain * (period - 1) + gains[i]) / period;
            avgLoss = (avgLoss * (period - 1) + losses[i]) / period;
            rsi[i] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
        }

        return rsi;
    }
}
