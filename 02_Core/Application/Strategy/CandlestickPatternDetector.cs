using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Professional-grade Candlestick Pattern Recognition Engine.
/// Detects price action reversals using OHLC price relationships.
/// </summary>
public static class CandlestickPatternDetector
{
    private const decimal DojiThreshold = 0.1m;
    private const decimal IndecisionThreshold = 0.3m;
    private const decimal StrongBodyThreshold = 0.5m;

    public static string DetectPattern(List<Candle> candles, int index = -1)
    {
        if (candles == null || candles.Count < 3) return "None";
        if (index == -1) index = candles.Count - 1;
        if (index < 2 || index >= candles.Count) return "None";

        var c0 = candles[index];     // Current
        var c1 = candles[index - 1]; // Previous
        var c2 = candles[index - 2]; // 2 ago

        // 1. DOJI
        var doji = DetectDoji(c0);
        if (doji != "None") return doji;

        // 2. HAMMER / HANGING MAN
        var hammer = DetectHammerLike(c0);
        if (hammer != "None") return hammer;

        // 3. SHOOTING STAR
        var shootingStar = DetectShootingStar(c0);
        if (shootingStar != "None") return shootingStar;

        // 4. ENGULFING
        var engulfing = DetectEngulfing(c0, c1);
        if (engulfing != "None") return engulfing;

        // 5. MORNING / EVENING STAR
        var star = DetectStarPattern(c0, c1, c2);
        if (star != "None") return star;

        return "None";
    }

    private static string DetectDoji(Candle c)
    {
        decimal body = Math.Abs(c.Close - c.Open);
        decimal range = Math.Max(c.High - c.Low, 0.00000001m);
        
        if (body > range * DojiThreshold) return "None";

        decimal upperWick = c.High - Math.Max(c.Open, c.Close);
        decimal lowerWick = Math.Min(c.Open, c.Close) - c.Low;

        if (lowerWick > range * 0.6m) return "Dragonfly Doji (Bullish)";
        if (upperWick > range * 0.6m) return "Gravestone Doji (Bearish)";
        
        return "Standard Doji (Neutral)";
    }

    private static string DetectHammerLike(Candle c)
    {
        decimal body = Math.Abs(c.Close - c.Open);
        decimal range = Math.Max(c.High - c.Low, 0.00000001m);
        decimal upperWick = c.High - Math.Max(c.Open, c.Close);
        decimal lowerWick = Math.Min(c.Open, c.Close) - c.Low;

        bool isHammerShape = lowerWick >= body * 2 && upperWick <= range * 0.1m;
        if (!isHammerShape) return "None";

        return IsBullish(c) ? "Bullish Hammer" : "Bearish Hammer (Hanging Man)";
    }

    private static string DetectShootingStar(Candle c)
    {
        decimal body = Math.Abs(c.Close - c.Open);
        decimal range = Math.Max(c.High - c.Low, 0.00000001m);
        decimal upperWick = c.High - Math.Max(c.Open, c.Close);
        decimal lowerWick = Math.Min(c.Open, c.Close) - c.Low;

        bool isShootingStarShape = upperWick >= body * 2 && lowerWick <= range * 0.1m;
        return isShootingStarShape ? "Shooting Star (Bearish)" : "None";
    }

    private static string DetectEngulfing(Candle current, Candle previous)
    {
        decimal body0 = Math.Abs(current.Close - current.Open);
        decimal body1 = Math.Abs(previous.Close - previous.Open);

        // Bullish Engulfing
        if (IsBearish(previous) && IsBullish(current) && 
            current.Open <= previous.Close && current.Close >= previous.Open &&
            body0 > body1)
        {
            return "Bullish Engulfing";
        }

        // Bearish Engulfing
        if (IsBullish(previous) && IsBearish(current) && 
            current.Open >= previous.Close && current.Close <= previous.Open &&
            body0 > body1)
        {
            return "Bearish Engulfing";
        }

        return "None";
    }

    private static string DetectStarPattern(Candle c0, Candle c1, Candle c2)
    {
        decimal range1 = Math.Max(c1.High - c1.Low, 0.00000001m);
        decimal body1 = Math.Abs(c1.Close - c1.Open);
        bool c1Indecision = body1 <= range1 * IndecisionThreshold;
        
        if (!c1Indecision) return "None";

        // Morning Star
        if (IsStrongBearish(c2) && IsBullish(c0) && 
            c0.Close > c2.Open - (c2.Open - c2.Close) / 2m)
        {
            return "Morning Star (Bullish Reversal)";
        }

        // Evening Star
        if (IsStrongBullish(c2) && IsBearish(c0) && 
            c0.Close < c2.Open + (c2.Close - c2.Open) / 2m)
        {
            return "Evening Star (Bearish Reversal)";
        }

        return "None";
    }

    private static bool IsBullish(Candle c) => c.Close > c.Open;
    private static bool IsBearish(Candle c) => c.Close < c.Open;

    private static bool IsStrongBullish(Candle c) => 
        IsBullish(c) && Math.Abs(c.Open - c.Close) > (c.High - c.Low) * StrongBodyThreshold;

    private static bool IsStrongBearish(Candle c) => 
        IsBearish(c) && Math.Abs(c.Open - c.Close) > (c.High - c.Low) * StrongBodyThreshold;
}
