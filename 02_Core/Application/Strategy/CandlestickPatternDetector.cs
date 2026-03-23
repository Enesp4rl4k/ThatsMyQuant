using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Professional-grade Candlestick Pattern Recognition Engine.
/// Detects price action reversals using OHLC price relationships.
/// </summary>
public static class CandlestickPatternDetector
{
    public static string DetectPattern(List<Candle> candles, int index = -1)
    {
        if (candles == null || candles.Count < 3) return "None";
        if (index == -1) index = candles.Count - 1;
        if (index < 2 || index >= candles.Count) return "None";

        var c0 = candles[index];     // Current
        var c1 = candles[index - 1]; // Previous
        var c2 = candles[index - 2]; // 2 ago

        // --- Current Candle Properties ---
        decimal body0 = Math.Abs(c0.Close - c0.Open);
        decimal range0 = Math.Max(c0.High - c0.Low, 0.00000001m);
        decimal upperWick0 = c0.High - Math.Max(c0.Open, c0.Close);
        decimal lowerWick0 = Math.Min(c0.Open, c0.Close) - c0.Low;
        bool isBullish0 = c0.Close > c0.Open;
        bool isBearish0 = c0.Close < c0.Open;

        // --- Previous Candle Properties ---
        decimal body1 = Math.Abs(c1.Close - c1.Open);
        decimal range1 = Math.Max(c1.High - c1.Low, 0.00000001m);
        bool isBullish1 = c1.Close > c1.Open;
        bool isBearish1 = c1.Close < c1.Open;

        // 1. DOJI (Neutral/Reversal)
        if (body0 <= range0 * 0.1m)
        {
            if (lowerWick0 > range0 * 0.6m) return "Dragonfly Doji (Bullish)";
            if (upperWick0 > range0 * 0.6m) return "Gravestone Doji (Bearish)";
            return "Standard Doji (Neutral)";
        }

        // 2. HAMMER (Bullish Reversal)
        // Body in upper 30%, long lower wick, little upper wick
        bool isHammerShape = lowerWick0 >= body0 * 2 && upperWick0 <= range0 * 0.1m;
        if (isHammerShape)
        {
            return isBullish0 ? "Bullish Hammer" : "Bearish Hammer (Hanging Man)";
        }

        // 3. SHOOTING STAR (Bearish Reversal)
        bool isShootingStarShape = upperWick0 >= body0 * 2 && lowerWick0 <= range0 * 0.1m;
        if (isShootingStarShape)
        {
            return "Shooting Star (Bearish)";
        }

        // 4. BULLISH ENGULFING
        if (isBearish1 && isBullish0 && c0.Open <= c1.Close && c0.Close >= c1.Open)
        {
            return "Bullish Engulfing";
        }

        // 5. BEARISH ENGULFING
        if (isBullish1 && isBearish0 && c0.Open >= c1.Close && c0.Close <= c1.Open)
        {
            return "Bearish Engulfing";
        }

        // 6. MORNING STAR (3-Candle Bullish)
        bool c2StrongBear = isBearish(c2) && Math.Abs(c2.Open - c2.Close) > (c2.High - c2.Low) * 0.5m;
        bool c1Indecision = body1 <= range1 * 0.3m;
        if (c2StrongBear && c1Indecision && isBullish0 && c0.Close > c2.Open - (c2.Open - c2.Close) / 2m)
        {
            return "Morning Star (Bullish Reversal)";
        }

        // 7. EVENING STAR (3-Candle Bearish)
        bool c2StrongBull = isBullish(c2) && Math.Abs(c2.Open - c2.Close) > (c2.High - c2.Low) * 0.5m;
        if (c2StrongBull && c1Indecision && isBearish0 && c0.Close < c2.Open + (c2.Close - c2.Open) / 2m)
        {
            return "Evening Star (Bearish Reversal)";
        }

        return "None";
    }

    private static bool isBullish(Candle c) => c.Close > c.Open;
    private static bool isBearish(Candle c) => c.Close < c.Open;
}