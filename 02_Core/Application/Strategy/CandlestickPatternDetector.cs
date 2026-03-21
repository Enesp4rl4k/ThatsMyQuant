using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

public static class CandlestickPatternDetector
{
    public static string DetectPattern(List<Candle> candles, int index = -1)
    {
        if (candles.Count < 3) return "None";
        if (index == -1) index = candles.Count - 1; // Default to last candle
        if (index < 2) return "None";

        var c0 = candles[index];      // Current
        var c1 = candles[index - 1];  // Previous
        var c2 = candles[index - 2];  // 2 ago

        // Helper calculations for Current Candle
        decimal body0 = Math.Abs(c0.Close - c0.Open);
        decimal range0 = c0.High - c0.Low;
        if (range0 == 0) range0 = 1; // Prevent div by zero
        decimal upperWick0 = c0.High - Math.Max(c0.Open, c0.Close);
        decimal lowerWick0 = Math.Min(c0.Open, c0.Close) - c0.Low;
        bool isBullish0 = c0.Close > c0.Open;
        bool isBearish0 = c0.Close < c0.Open;

        // Helper calculations for Previous Candle
        decimal body1 = Math.Abs(c1.Close - c1.Open);
        bool isBullish1 = c1.Close > c1.Open;
        bool isBearish1 = c1.Close < c1.Open;

        // 1. DOJI
        // Body is less than 10% of total range
        if (body0 <= range0 * 0.1m)
        {
            if (lowerWick0 > range0 * 0.6m) return "Dragonfly Doji (Bullish)";
            if (upperWick0 > range0 * 0.6m) return "Gravestone Doji (Bearish)";
            return "Standard Doji (Neutral)";
        }

        // 2. HAMMER (Bullish) / HANGING MAN (Bearish context, usually at top)
        // Small body in upper 30% of range, long lower wick (>= 2x body), tiny/no upper wick
        if (lowerWick0 >= body0 * 2 && upperWick0 <= body0 * 0.2m && (c0.High - Math.Max(c0.Open, c0.Close)) <= range0 * 0.2m)
        {
            // For simplicity, we just label it Hammer
            return isBullish0 ? "Bullish Hammer" : "Bearish Hammer";
        }

        // 3. SHOOTING STAR (Bearish) / INVERTED HAMMER
        // Small body in lower 30% of range, long upper wick (>= 2x body), tiny/no lower wick
        if (upperWick0 >= body0 * 2 && lowerWick0 <= body0 * 0.2m && (Math.Min(c0.Open, c0.Close) - c0.Low) <= range0 * 0.2m)
        {
            return "Shooting Star (Bearish)";
        }

        // 4. BULLISH ENGULFING
        // Previous was bearish, current is bullish and completely engulfs previous body
        if (isBearish1 && isBullish0 && c0.Open < c1.Close && c0.Close > c1.Open)
        {
            return "Bullish Engulfing";
        }

        // 5. BEARISH ENGULFING
        // Previous was bullish, current is bearish and completely engulfs previous body
        if (isBullish1 && isBearish0 && c0.Open > c1.Close && c0.Close < c1.Open)
        {
            return "Bearish Engulfing";
        }

        // 6. MORNING STAR (3-candle bullish reversal)
        // c2: Strong bearish, c1: small body/doji gapping down, c0: strong bullish closing well into c2's body
        if (isBearish2(c2) && body1 <= range1(c1) * 0.3m && isBullish0 && c0.Close > c2.Open - (c2.Open - c2.Close) / 2m)
        {
            return "Morning Star (Bullish Reversal)";
        }

        // 7. EVENING STAR (3-candle bearish reversal)
        // c2: Strong bullish, c1: small body/doji gapping up, c0: strong bearish closing well into c2's body
        if (isBullish2(c2) && body1 <= range1(c1) * 0.3m && isBearish0 && c0.Close < c2.Open + (c2.Close - c2.Open) / 2m)
        {
            return "Evening Star (Bearish Reversal)";
        }

        return "None";
    }

    // Small helpers for 3-candle patterns
    private static bool isBearish2(Candle c) => c.Close < c.Open && Math.Abs(c.Open - c.Close) > (c.High - c.Low) * 0.5m;
    private static bool isBullish2(Candle c) => c.Close > c.Open && Math.Abs(c.Open - c.Close) > (c.High - c.Low) * 0.5m;
    private static decimal range1(Candle c) => Math.Max(c.High - c.Low, 1m);
}
