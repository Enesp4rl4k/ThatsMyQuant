using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Infrastructure.Indicators;

/// <summary>
/// C# port of only_fibonacci's Absorbing System indicator (Pine Script v5).
///
/// Logic:
///   1. Detect pivot highs/lows (lookback = pivotLen)
///   2. Filter by wick absorption: wick > body size = absorption candle
///      - Resistance: upper wick > lower wick AND upper wick > body
///      - Support: lower wick > upper wick AND lower wick > body
///   3. Track S/R levels, detect breakdowns when price crosses through
///   4. Return active (unbroken) S/R levels
/// </summary>
public static class AbsorbingSystemCalculator
{
    public static List<SupportResistanceLevel> Calculate(List<Candle> candles, int pivotLen = 10)
    {
        var levels = new List<SupportResistanceLevel>();
        if (candles.Count < pivotLen * 2 + 1) return levels;

        var resistanceIdxs = FindAbsorptionPivotIndexes(candles, pivotLen, isHigh: true);
        var supportIdxs = FindAbsorptionPivotIndexes(candles, pivotLen, isHigh: false);

        AddLevels(candles, resistanceIdxs, SRType.Resistance, levels);
        AddLevels(candles, supportIdxs, SRType.Support, levels);

        return levels;
    }

    private static List<int> FindAbsorptionPivotIndexes(List<Candle> candles, int pivotLen, bool isHigh)
    {
        var indexes = new List<int>();
        int count = candles.Count;

        for (int i = pivotLen; i < count - pivotLen; i++)
        {
            if (IsPivot(candles, i, pivotLen, isHigh) && IsAbsorption(candles[i], isHigh))
            {
                indexes.Add(i);
                if (indexes.Count > 5) indexes.RemoveAt(0);
            }
        }
        return indexes;
    }

    private static bool IsPivot(List<Candle> candles, int index, int pivotLen, bool isHigh)
    {
        for (int j = 1; j <= pivotLen; j++)
        {
            if (isHigh)
            {
                if (candles[index].High <= candles[index - j].High || candles[index].High <= candles[index + j].High)
                    return false;
            }
            else
            {
                if (candles[index].Low >= candles[index - j].Low || candles[index].Low >= candles[index + j].Low)
                    return false;
            }
        }
        return true;
    }

    private static bool IsAbsorption(Candle candle, bool isHigh)
    {
        decimal body = Math.Abs(candle.Close - candle.Open);
        decimal upperWick = candle.High - Math.Max(candle.Open, candle.Close);
        decimal lowerWick = Math.Min(candle.Open, candle.Close) - candle.Low;

        return isHigh 
            ? upperWick > lowerWick && upperWick > body 
            : lowerWick > upperWick && lowerWick > body;
    }

    private static void AddLevels(List<Candle> candles, List<int> indexes, SRType type, List<SupportResistanceLevel> levels)
    {
        foreach (var idx in indexes)
        {
            decimal price = type == SRType.Resistance ? candles[idx].High : candles[idx].Low;
            var (isBroken, testCount) = DetectBreakdown(candles, idx, price, type);

            levels.Add(new SupportResistanceLevel
            {
                Price = price,
                Type = type,
                BarIndex = idx,
                IsBroken = isBroken,
                TestCount = testCount,
                DetectedAt = candles[idx].Timestamp
            });
        }
    }

    private static (bool IsBroken, int TestCount) DetectBreakdown(List<Candle> candles, int startIdx, decimal price, SRType type)
    {
        bool isBroken = false;
        int testCount = 0;

        for (int j = startIdx + 1; j < candles.Count; j++)
        {
            var candle = candles[j];
            if (IsPriceBroken(candle, price, type))
            {
                isBroken = true;
                break;
            }
            if (IsPriceTested(candle, price, type)) testCount++;
        }
        return (isBroken, testCount);
    }

    private static bool IsPriceBroken(Candle c, decimal price, SRType type) =>
        type == SRType.Resistance ? c.Close > price : c.Close < price;

    private static bool IsPriceTested(Candle c, decimal price, SRType type)
    {
        if (type == SRType.Resistance)
        {
            decimal highBody = Math.Max(c.Open, c.Close);
            return highBody >= price * 0.998m && c.Close <= price;
        }
        else
        {
            decimal lowBody = Math.Min(c.Open, c.Close);
            return lowBody <= price * 1.002m && c.Close >= price;
        }
    }

    /// <summary>
    /// Get the nearest active (unbroken) support and resistance levels to the current price.
    /// </summary>
    public static (SupportResistanceLevel? NearestSupport, SupportResistanceLevel? NearestResistance) GetNearestLevels(
        List<SupportResistanceLevel> levels, decimal currentPrice)
    {
        var activeLevels = levels.Where(l => !l.IsBroken).ToList();

        var nearestSupport = activeLevels
            .Where(l => l.Type == SRType.Support && l.Price < currentPrice)
            .OrderByDescending(l => l.Price)
            .FirstOrDefault();

        var nearestResistance = activeLevels
            .Where(l => l.Type == SRType.Resistance && l.Price > currentPrice)
            .OrderBy(l => l.Price)
            .FirstOrDefault();

        return (nearestSupport, nearestResistance);
    }
}
