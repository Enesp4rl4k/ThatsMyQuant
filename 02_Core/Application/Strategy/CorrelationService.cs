using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ThatsMyQuant.Application.Strategy;

public static class CorrelationService
{
    // SonarQube Fix: Sözlüğü readonly yaparak thread-safety sağladık.
    private static readonly Dictionary<string, string> StockSectors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "GARAN", "Banking" }, { "AKBNK", "Banking" }, { "YKBNK", "Banking" },
        { "ISCTR", "Banking" }, { "HALKB", "Banking" }, { "VAKBN", "Banking" },
        { "KCHOL", "Holding" }, { "SAHOL", "Holding" }, { "TAVHL", "Holding" },
        { "THYAO", "Aviation" }, { "PGSUS", "Aviation" },
        { "TUPRS", "Energy" }, { "AYGAZ", "Energy" },
        { "EREGL", "Metal" }, { "KRDMD", "Metal" },
        { "SISE", "Industry" }, { "SASA", "Industry" },
        { "BIMAS", "Retail" }, { "MGROS", "Retail" },
        { "ASELS", "Defense" },
        { "TCELL", "Telecom" }, { "TTKOM", "Telecom" },
        { "BTC", "Crypto" }, { "ETH", "Crypto" }, { "SOL", "Crypto" }, { "XRP", "Crypto" },
        { "USDTRY", "Forex" }, { "EURTRY", "Forex" }, { "EURUSD", "Forex" }, { "GBPUSD", "Forex" }
    };

    public static CorrelationCheckResult CheckCorrelation(
        string newSymbol,
        List<string> currentPositionSymbols,
        double maxSectorExposurePercent = 40.0)
    {
        string newSector = GetSector(newSymbol);
        
        // Count positions in the same sector using a simple loop for performance
        int sameSecPositions = 1; 
        foreach (var sym in currentPositionSymbols)
        {
            if (GetSector(sym).Equals(newSector, StringComparison.OrdinalIgnoreCase))
            {
                sameSecPositions++;
            }
        }

        int totalPositions = currentPositionSymbols.Count + 1;
        double sectorExposure = (sameSecPositions * 100.0) / totalPositions;

        bool isCorrelated = sameSecPositions > 1;
        bool exceedsLimit = sectorExposure > maxSectorExposurePercent;

        // Structured recommendation building
        string rec = GetRecommendation(newSector, sectorExposure, isCorrelated, exceedsLimit);

        return new CorrelationCheckResult
        {
            Symbol = newSymbol,
            Sector = newSector,
            SectorExposurePercent = sectorExposure,
            SameSecPositionCount = sameSecPositions,
            IsHighlyCorrelated = isCorrelated,
            ExceedsSectorLimit = exceedsLimit,
            ShouldReduce = exceedsLimit,
            Recommendation = rec
        };
    }

    private static string GetRecommendation(string sector, double exposure, bool isCorrelated, bool exceedsLimit)
    {
        if (exceedsLimit) return $"⚠️ {sector} sector at {exposure:F0}% — too concentrated. Reduce position or skip.";
        if (isCorrelated) return $"⚡ {sector} sector at {exposure:F0}% — acceptable but watch exposure.";
        return $"✅ {sector} — no sector concentration risk.";
    }

    public static string GetSector(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return "Other";

        // SonarQube Fix: Daha güvenli string parçalama logic'i
        string cleanSymbol = symbol.Split(new[] { '-', '=', '/' }, StringSplitOptions.RemoveEmptyEntries)[0].ToUpper();
        
        return StockSectors.TryGetValue(cleanSymbol, out var sector) ? sector : "Other";
    }

    public static double CalculateCorrelation(double[] seriesA, double[] seriesB)
    {
        if (seriesA == null || seriesB == null || seriesA.Length != seriesB.Length || seriesA.Length == 0) 
            return 0;

        int length = seriesA.Length;
        double avgA = seriesA.Average();
        double avgB = seriesB.Average();

        int vectorSize = Vector<double>.Count;
        var vAvgA = new Vector<double>(avgA);
        var vAvgB = new Vector<double>(avgB);
        
        var vSumAb = Vector<double>.Zero;
        var vSumA2 = Vector<double>.Zero;
        var vSumB2 = Vector<double>.Zero;

        int i = 0;
        for (; i <= length - vectorSize; i += vectorSize)
        {
            var vA = new Vector<double>(seriesA, i);
            var vB = new Vector<double>(seriesB, i);

            var vDiffA = vA - vAvgA;
            var vDiffB = vB - vAvgB;

            vSumAb += vDiffA * vDiffB;
            vSumA2 += vDiffA * vDiffA;
            vSumB2 += vDiffB * vDiffB;
        }

        double sumAb = Vector.Sum(vSumAb);
        double sumA2 = Vector.Sum(vSumA2);
        double sumB2 = Vector.Sum(vSumB2);

        // Remainder
        for (; i < length; i++)
        {
            double diffA = seriesA[i] - avgA;
            double diffB = seriesB[i] - avgB;
            sumAb += diffA * diffB;
            sumA2 += diffA * diffA;
            sumB2 += diffB * diffB;
        }

        double denominator = Math.Sqrt(sumA2 * sumB2);
        return denominator < double.Epsilon ? 0 : sumAb / denominator;
    }
}

public class CorrelationCheckResult
{
    public string Symbol { get; set; } = string.Empty;
    public string Sector { get; set; } = string.Empty;
    public double SectorExposurePercent { get; set; }
    public int SameSecPositionCount { get; set; }
    public bool IsHighlyCorrelated { get; set; }
    public bool ExceedsSectorLimit { get; set; }
    public bool ShouldReduce { get; set; }
    public string Recommendation { get; set; } = string.Empty;
}