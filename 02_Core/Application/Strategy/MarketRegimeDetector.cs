using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Detects the current market regime (Trending, Ranging, or Volatile)
/// to adapt indicator parameters for better accuracy.
///
/// Methods:
///   - ADX > 25 + EMA alignment → Trending
///   - ATR > 2x average → Volatile
///   - Otherwise → Ranging
///
/// Adaptive parameter adjustments:
///   - Trending: RSI(10), EMA(15/40) — more responsive to ride trends
///   - Ranging: RSI(20), EMA(20/50) — slower to avoid whipsaws
///   - Volatile: RSI(14), EMA(20/50) — standard but with wider stops (3x ATR)
/// </summary>
public static class MarketRegimeDetector
{
    public record RegimeResult
    {
        public MarketRegime Regime { get; init; }
        public int AdaptedRsiPeriod { get; init; }
        public int AdaptedEmaFast { get; init; }
        public int AdaptedEmaSlow { get; init; }
        public double StopLossMultiplier { get; init; }
        public double TakeProfitMultiplier { get; init; }
        public string Description { get; init; } = "";
    }

    public static RegimeResult Detect(IReadOnlyList<Candle> candles, int lookback = 20)
    {
        if (candles.Count < lookback + 14)
            return new RegimeResult { Regime = MarketRegime.Unknown, AdaptedRsiPeriod = 14,
                AdaptedEmaFast = 20, AdaptedEmaSlow = 50, StopLossMultiplier = 2.0,
                TakeProfitMultiplier = 3.0, Description = "Insufficient data" };

        // Compute ATR for volatility assessment
        var recentCandles = candles.TakeLast(lookback).ToList();
        double avgAtr = ComputeAverageATR(recentCandles);
        double avgPrice = recentCandles.Average(c => (double)c.Close);
        double atrPercent = avgPrice > 0 ? avgAtr / avgPrice * 100 : 0;

        // Compute directional movement for trend assessment
        double trendStrength = ComputeTrendStrength(recentCandles);

        // ADX proxy: strong trend if consecutive higher closes or lower closes AND low volatility
        // Require atrPercent < 1.5 so a "choppy" volatile market doesn't get the Trending regime bonus.
        bool isTrending = trendStrength > 0.55 && atrPercent < 1.5;
        bool isVolatile = atrPercent > 3.0; // ATR > 3% of price = volatile

        if (isVolatile)
        {
            return new RegimeResult
            {
                Regime = MarketRegime.Volatile,
                AdaptedRsiPeriod = 14,
                AdaptedEmaFast = 20,
                AdaptedEmaSlow = 50,
                StopLossMultiplier = 3.0,  // Wider stops in volatile markets
                TakeProfitMultiplier = 4.5,
                Description = $"VOLATILE — ATR={atrPercent:F1}% of price. Wider stops, smaller positions."
            };
        }

        if (isTrending)
        {
            return new RegimeResult
            {
                Regime = MarketRegime.Trending,
                AdaptedRsiPeriod = 10,     // More responsive RSI
                AdaptedEmaFast = 15,       // Faster EMA to ride trends
                AdaptedEmaSlow = 40,
                StopLossMultiplier = 2.0,
                TakeProfitMultiplier = 4.0, // Let winners run longer in trends
                Description = $"TRENDING — {trendStrength:P0} directional consistency. Riding the trend."
            };
        }

        return new RegimeResult
        {
            Regime = MarketRegime.Ranging,
            AdaptedRsiPeriod = 20,     // Slower RSI to avoid false signals
            AdaptedEmaFast = 20,
            AdaptedEmaSlow = 50,
            StopLossMultiplier = 1.5,  // Tighter stops in range
            TakeProfitMultiplier = 2.0,
            Description = $"RANGING — {trendStrength:P0} directional. Mean-reversion mode."
        };
    }

    private static double ComputeAverageATR(IReadOnlyList<Candle> candles)
    {
        double totalTR = 0;
        for (int i = 1; i < candles.Count; i++)
        {
            double hl = (double)(candles[i].High - candles[i].Low);
            double hc = Math.Abs((double)(candles[i].High - candles[i - 1].Close));
            double lc = Math.Abs((double)(candles[i].Low - candles[i - 1].Close));
            totalTR += Math.Max(hl, Math.Max(hc, lc));
        }
        return candles.Count > 1 ? totalTR / (candles.Count - 1) : 0;
    }

    private static double ComputeTrendStrength(IReadOnlyList<Candle> candles)
    {
        int upBars = 0, downBars = 0;
        for (int i = 1; i < candles.Count; i++)
        {
            if (candles[i].Close > candles[i - 1].Close) upBars++;
            else downBars++;
        }
        int total = upBars + downBars;
        return total > 0 ? Math.Max(upBars, downBars) / (double)total : 0.5;
    }
}
