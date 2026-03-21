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
        int count = candles.Count;
        var levels = new List<SupportResistanceLevel>();

        if (count < pivotLen * 2 + 1) return levels;

        // Step 1: Find resistance zones (pivot highs with absorption candles)
        var resistanceIndexes = new List<int>();
        // Step 2: Find support zones (pivot lows with absorption candles)
        var supportIndexes = new List<int>();

        for (int i = pivotLen; i < count - pivotLen; i++)
        {
            // Check if this bar is a pivot high
            bool isPivotHigh = true;
            for (int j = 1; j <= pivotLen; j++)
            {
                if (candles[i].High <= candles[i - j].High || candles[i].High <= candles[i + j].High)
                {
                    isPivotHigh = false;
                    break;
                }
            }

            // Check if this bar is a pivot low
            bool isPivotLow = true;
            for (int j = 1; j <= pivotLen; j++)
            {
                if (candles[i].Low >= candles[i - j].Low || candles[i].Low >= candles[i + j].Low)
                {
                    isPivotLow = false;
                    break;
                }
            }

            // Absorption candle analysis (wick vs body)
            var candle = candles[i];
            decimal open = candle.Open;
            decimal close = candle.Close;
            decimal high = candle.High;
            decimal low = candle.Low;
            decimal body = Math.Abs(close - open);
            decimal upperWick = high - Math.Max(open, close);
            decimal lowerWick = Math.Min(open, close) - low;

            bool isAbsorptionRes = upperWick > lowerWick && upperWick > body;
            bool isAbsorptionSup = lowerWick > upperWick && lowerWick > body;

            if (isPivotHigh && isAbsorptionRes)
            {
                resistanceIndexes.Add(i);
                // Keep only last 5
                if (resistanceIndexes.Count > 5)
                    resistanceIndexes.RemoveAt(0);
            }

            if (isPivotLow && isAbsorptionSup)
            {
                supportIndexes.Add(i);
                if (supportIndexes.Count > 5)
                    supportIndexes.RemoveAt(0);
            }
        }

        // Step 3: Check each S/R level for breakdowns
        foreach (var idx in resistanceIndexes)
        {
            decimal resPrice = candles[idx].High;
            bool isBroken = false;
            int testCount = 0;

            // Check from detection to end of data
            for (int j = idx + 1; j < count; j++)
            {
                // Test: price approached but didn't break
                decimal highBody = Math.Max(candles[j].Open, candles[j].Close);
                if (highBody >= resPrice * 0.998m && candles[j].Close <= resPrice)
                {
                    testCount++;
                }

                // Breakdown: close above resistance
                if (candles[j].Close > resPrice)
                {
                    isBroken = true;
                    break;
                }
            }

            levels.Add(new SupportResistanceLevel
            {
                Price = resPrice,
                Type = SRType.Resistance,
                BarIndex = idx,
                IsBroken = isBroken,
                TestCount = testCount,
                DetectedAt = candles[idx].Timestamp
            });
        }

        foreach (var idx in supportIndexes)
        {
            decimal supPrice = candles[idx].Low;
            bool isBroken = false;
            int testCount = 0;

            for (int j = idx + 1; j < count; j++)
            {
                decimal lowBody = Math.Min(candles[j].Open, candles[j].Close);
                if (lowBody <= supPrice * 1.002m && candles[j].Close >= supPrice)
                {
                    testCount++;
                }

                if (candles[j].Close < supPrice)
                {
                    isBroken = true;
                    break;
                }
            }

            levels.Add(new SupportResistanceLevel
            {
                Price = supPrice,
                Type = SRType.Support,
                BarIndex = idx,
                IsBroken = isBroken,
                TestCount = testCount,
                DetectedAt = candles[idx].Timestamp
            });
        }

        return levels;
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
