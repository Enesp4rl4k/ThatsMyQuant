using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public enum MarketStructure
{
    Ranging,
    BullTrap,
    BearTrap,
    Breakout,
    Unknown
}

public class StructuralAnalysisResult
{
    public MarketStructure Structure { get; set; } = MarketStructure.Unknown;
    public decimal SupportLevel { get; set; }
    public decimal ResistanceLevel { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Detects advanced market structure patterns like Ranges and Liquidity Sweeps (Bull/Bear Traps).
/// Optimized for higher timeframe structural analysis (e.g., 4h/1d).
/// </summary>
public static class StructuralPatternDetector
{
    public static StructuralAnalysisResult Analyze(IReadOnlyList<Candle> candles, int lookback = 100)
    {
        if (candles.Count < 20)
            return new StructuralAnalysisResult { Description = "Insufficient data" };

        var recentCandles = candles.TakeLast(lookback).ToList();
        
        // 1. Identify Local Swing Highs and Lows to find potential Ranges
        var highs = IdentifySwingPoints(recentCandles, isHigh: true);
        var lows = IdentifySwingPoints(recentCandles, isHigh: false);

        // 2. Cluster these points to find Horizontal Support & Resistance
        decimal resistance = ClusterPoints(highs);
        decimal support = ClusterPoints(lows);

        if (resistance <= support)
            return new StructuralAnalysisResult { Structure = MarketStructure.Unknown, Description = "Price structure not clearly defined." };

        // 3. Detect Traps (Liquidity Sweeps)
        // A Bull Trap: Most recent high breached the resistance zone but closed back inside it.
        var currentCandle = recentCandles.Last();
        var prevCandle = recentCandles[recentCandles.Count - 2];

        bool reachedAboveResistance = recentCandles.TakeLast(10).Any(c => c.High > resistance * 1.01m);
        bool currentlyInsideOrBelow = currentCandle.Close < resistance;
        bool sharpRejection = currentCandle.Close < currentCandle.Open && (currentCandle.High - currentCandle.Open) > (currentCandle.Open - currentCandle.Close);

        if (reachedAboveResistance && currentlyInsideOrBelow && sharpRejection)
        {
            return new StructuralAnalysisResult
            {
                Structure = MarketStructure.BullTrap,
                ResistanceLevel = resistance,
                SupportLevel = support,
                Description = $"BULL TRAP detected. Price breached {resistance:F2} but was sharply rejected. High liquidity area."
            };
        }

        // Bear Trap
        bool reachedBelowSupport = recentCandles.TakeLast(10).Any(c => c.Low < support * 0.99m);
        bool currentlyInsideOrAbove = currentCandle.Close > support;

        if (reachedBelowSupport && currentlyInsideOrAbove && currentCandle.Close > currentCandle.Open)
        {
            return new StructuralAnalysisResult
            {
                Structure = MarketStructure.BearTrap,
                ResistanceLevel = resistance,
                SupportLevel = support,
                Description = $"BEAR TRAP detected. Price dipped below {support:F2} and recovered. Bullish sign."
            };
        }

        return new StructuralAnalysisResult
        {
            Structure = MarketStructure.Ranging,
            ResistanceLevel = resistance,
            SupportLevel = support,
            Description = $"RANGING. Price bouncing between {support:F2} and {resistance:F2}."
        };
    }

    private static List<decimal> IdentifySwingPoints(IReadOnlyList<Candle> candles, bool isHigh)
    {
        var points = new List<decimal>();
        // Simple 5-candle fractal detection
        for (int i = 2; i < candles.Count - 2; i++)
        {
            if (isHigh)
            {
                if (candles[i].High > candles[i - 1].High && 
                    candles[i].High > candles[i - 2].High && 
                    candles[i].High > candles[i + 1].High && 
                    candles[i].High > candles[i + 2].High)
                    points.Add(candles[i].High);
            }
            else
            {
                if (candles[i].Low < candles[i - 1].Low && 
                    candles[i].Low < candles[i - 2].Low && 
                    candles[i].Low < candles[i + 1].Low && 
                    candles[i].Low < candles[i + 2].Low)
                    points.Add(candles[i].Low);
            }
        }
        return points;
    }

    private static decimal ClusterPoints(List<decimal> points)
    {
        if (!points.Any()) return 0;
        
        // Simple clustering: find the most frequent price area
        // For a small set of points, we take the average of the 3 most recent significant swings
        return points.TakeLast(3).Average();
    }
}
