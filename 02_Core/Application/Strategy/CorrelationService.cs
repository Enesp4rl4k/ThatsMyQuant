namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Portfolio-level correlation and sector exposure management for BIST stocks.
///
/// Prevents concentrated risk by:
///   1. Grouping stocks by sector
///   2. Limiting max exposure per sector (default 30%)
///   3. Checking correlation before opening new positions
/// </summary>
public static class CorrelationService
{
    // BIST sector classification for major stocks
    private static readonly Dictionary<string, string> StockSectors = new()
    {
        // Banking
        { "GARAN", "Banking" }, { "AKBNK", "Banking" }, { "YKBNK", "Banking" },
        { "ISCTR", "Banking" }, { "HALKB", "Banking" }, { "VAKBN", "Banking" },
        // Industry/Holding
        { "KCHOL", "Holding" }, { "SAHOL", "Holding" }, { "TAVHL", "Holding" },
        // Aviation/Tourism
        { "THYAO", "Aviation" }, { "PGSUS", "Aviation" },
        // Energy
        { "TUPRS", "Energy" }, { "AYGAZ", "Energy" },
        // Metal/Mining
        { "EREGL", "Metal" }, { "KRDMD", "Metal" },
        // Glass/Chemistry
        { "SISE", "Industry" }, { "SASA", "Industry" },
        // Retail
        { "BIMAS", "Retail" }, { "MGROS", "Retail" },
        // Defense
        { "ASELS", "Defense" },
        // Telecom
        { "TCELL", "Telecom" }, { "TTKOM", "Telecom" },
        // Crypto
        { "BTC", "Crypto" }, { "ETH", "Crypto" }, { "SOL", "Crypto" }, { "XRP", "Crypto" },
        // Forex
        { "USDTRY", "Forex" }, { "EURTRY", "Forex" }, { "EURUSD", "Forex" }, { "GBPUSD", "Forex" }
    };

    /// <summary>
    /// Check if adding a position in this symbol would exceed sector concentration limits.
    /// </summary>
    public static CorrelationCheckResult CheckCorrelation(
        string newSymbol,
        List<string> currentPositionSymbols,
        double maxSectorExposurePercent = 40.0) // Increased slightly for multi-asset
    {
        string newSector = GetSector(newSymbol.Split('-')[0].Split('=')[0]); // Clean symbol (BTC-USD -> BTC)
        var positionSectors = currentPositionSymbols.Select(s => GetSector(s.Split('-')[0].Split('=')[0])).ToList();
        int totalPositions = currentPositionSymbols.Count + 1;

        // Count how many positions are in the same sector
        int sameSecPositions = positionSectors.Count(s => s == newSector) + 1; 
        double sectorExposure = totalPositions > 0 ? sameSecPositions * 100.0 / totalPositions : 0;

        bool isCorrelated = sameSecPositions > 1;
        bool exceedsLimit = sectorExposure > maxSectorExposurePercent;

        return new CorrelationCheckResult
        {
            Symbol = newSymbol,
            Sector = newSector,
            SectorExposurePercent = sectorExposure,
            SameSecPositionCount = sameSecPositions,
            IsHighlyCorrelated = isCorrelated,
            ExceedsSectorLimit = exceedsLimit,
            ShouldReduce = exceedsLimit,
            Recommendation = exceedsLimit
                ? $"⚠️ {newSector} sector at {sectorExposure:F0}% — too concentrated. Reduce position or skip."
                : isCorrelated
                    ? $"⚡ {newSector} sector at {sectorExposure:F0}% — acceptable but watch exposure."
                    : $"✅ {newSector} — no sector concentration risk."
        };
    }

    /// <summary>
    /// Get sector diversification summary for current portfolio.
    /// </summary>
    public static Dictionary<string, int> GetSectorBreakdown(List<string> symbols)
    {
        return symbols.GroupBy(GetSector).ToDictionary(g => g.Key, g => g.Count());
    }

    public static string GetSector(string symbol) =>
        StockSectors.TryGetValue(symbol.ToUpper(), out var sector) ? sector : "Other";

    /// <summary>
    /// Calculates the Pearson Correlation Coefficient between two price series.
    /// Values near 1.0 indicate moving in lockstep (bad for diversification).
    /// Values near 0.0 indicate independent movement (good for diversification).
    /// </summary>
    public static double CalculateCorrelation(double[] seriesA, double[] seriesB)
    {
        if (seriesA.Length != seriesB.Length || seriesA.Length == 0) return 0;

        int length = seriesA.Length;
        double avgA = seriesA.Average();
        double avgB = seriesB.Average();

        int vectorSize = System.Numerics.Vector<double>.Count;
        var vAvgA = new System.Numerics.Vector<double>(avgA);
        var vAvgB = new System.Numerics.Vector<double>(avgB);
        
        var vSumAb = System.Numerics.Vector<double>.Zero;
        var vSumA2 = System.Numerics.Vector<double>.Zero;
        var vSumB2 = System.Numerics.Vector<double>.Zero;

        int i = 0;
        // SIMD loop
        for (; i <= length - vectorSize; i += vectorSize)
        {
            var vA = new System.Numerics.Vector<double>(seriesA, i);
            var vB = new System.Numerics.Vector<double>(seriesB, i);

            var vDiffA = vA - vAvgA;
            var vDiffB = vB - vAvgB;

            vSumAb += vDiffA * vDiffB;
            vSumA2 += vDiffA * vDiffA;
            vSumB2 += vDiffB * vDiffB;
        }

        // Horizontal sum of vectors
        double sumAb = System.Numerics.Vector.Dot(vSumAb, System.Numerics.Vector<double>.One);
        double sumA2 = System.Numerics.Vector.Dot(vSumA2, System.Numerics.Vector<double>.One);
        double sumB2 = System.Numerics.Vector.Dot(vSumB2, System.Numerics.Vector<double>.One);

        // Remainder loop for non-vector-aligned data
        for (; i < length; i++)
        {
            double diffA = seriesA[i] - avgA;
            double diffB = seriesB[i] - avgB;
            sumAb += diffA * diffB;
            sumA2 += diffA * diffA;
            sumB2 += diffB * diffB;
        }

        double denominator = Math.Sqrt(sumA2 * sumB2);
        return denominator == 0 ? 0 : sumAb / denominator;
    }
}

public record CorrelationCheckResult
{
    public string Symbol { get; init; } = "";
    public string Sector { get; init; } = "";
    public double SectorExposurePercent { get; init; }
    public int SameSecPositionCount { get; init; }
    public bool IsHighlyCorrelated { get; init; }
    public bool ExceedsSectorLimit { get; init; }
    public bool ShouldReduce { get; init; }
    public string Recommendation { get; init; } = "";
}
