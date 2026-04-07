using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Options;

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

    public static RegimeResult Detect(IReadOnlyList<Candle> candles, int lookback = 20, MarketRegimeDetectionOptions? options = null)
    {
        var opts = options ?? new MarketRegimeDetectionOptions();

        if (candles.Count < lookback + 14)
            return new RegimeResult { Regime = MarketRegime.Unknown, AdaptedRsiPeriod = 14,
                AdaptedEmaFast = 20, AdaptedEmaSlow = 50, StopLossMultiplier = 2.0,
                TakeProfitMultiplier = 3.0, Description = "Insufficient data" };

        // Compute ATR for volatility assessment
        var recentCandles = candles.TakeLast(lookback).ToList();
        double avgAtr = ComputeAverageATR(recentCandles);
        double avgPrice = recentCandles.Average(c => (double)c.Close);
        double atrPercent = avgPrice > 0 ? avgAtr / avgPrice * 100 : 0;

        // Compute directional movement AND Shannon Entropy
        double trendStrength = ComputeTrendStrength(recentCandles);
        double normalizedEntropy = ComputeShannonEntropy(recentCandles);
        double garchVolatilityForecast = ComputeGarchVolatility(recentCandles, opts);

        // Shannon Entropy: < 0.82 means the returns distribution is organized (trending).
        // > 0.90 means it's mostly random noise (ranging).
        bool isTrending = normalizedEntropy < opts.ShannonEntropyThreshold && atrPercent < opts.AtrPercentLowThreshold && garchVolatilityForecast < opts.GarchLowThreshold;
        
        // Trigger volatile regime if ATR is huge OR if GARCH(1,1) forecasts an impending volatility cluster.
        bool isVolatile = atrPercent > opts.AtrPercentHighThreshold || garchVolatilityForecast > opts.GarchHighThreshold;

        if (isVolatile)
        {
            return new RegimeResult
            {
                Regime = MarketRegime.Volatile,
                AdaptedRsiPeriod = opts.VolatileRsiPeriod,
                AdaptedEmaFast = opts.VolatileEmaFast,
                AdaptedEmaSlow = opts.VolatileEmaSlow,
                StopLossMultiplier = opts.VolatileStopLossMultiplier,  // Wider stops in volatile markets
                TakeProfitMultiplier = opts.VolatileTakeProfitMultiplier,
                Description = $"VOLATILE — ATR={atrPercent:F1}%, GARCH={garchVolatilityForecast:F2}%. Impending/active volatility cluster detected."
            };
        }

        if (isTrending)
        {
            return new RegimeResult
            {
                Regime = MarketRegime.Trending,
                AdaptedRsiPeriod = opts.TrendingRsiPeriod,     // More responsive RSI
                AdaptedEmaFast = opts.TrendingEmaFast,       // Faster EMA to ride trends
                AdaptedEmaSlow = opts.TrendingEmaSlow,
                StopLossMultiplier = opts.TrendingStopLossMultiplier,
                TakeProfitMultiplier = opts.TrendingTakeProfitMultiplier, // Let winners run longer in trends
                Description = $"TRENDING — Entropy={normalizedEntropy:F2}. Organized structure. Riding the trend."
            };
        }

        return new RegimeResult
        {
            Regime = MarketRegime.Ranging,
            AdaptedRsiPeriod = opts.RangingRsiPeriod,     // Slower RSI to avoid false signals
            AdaptedEmaFast = opts.RangingEmaFast,
            AdaptedEmaSlow = opts.RangingEmaSlow,
            StopLossMultiplier = opts.RangingStopLossMultiplier,  // Tighter stops in range
            TakeProfitMultiplier = opts.RangingTakeProfitMultiplier,
            Description = $"RANGING — Entropy={normalizedEntropy:F2}. High noise {trendStrength:P0} directional. Mean-reversion mode."
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

    private static double ComputeShannonEntropy(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 2) return 1.0;

        var returns = GetLogReturns(candles);
        if (returns.Count == 0) return 1.0;

        double stdDev = GetStandardDeviation(returns);
        if (Math.Abs(stdDev) < 1e-9) return 0.0; // Flat line = perfect order

        var counts = DiscretizeReturns(returns, stdDev);
        double entropy = CalculateEntropyFromBins(counts, returns.Count);

        // Normalize Entropy: max entropy for 5 bins is log2(5)
        return entropy / Math.Log2(5);
    }

    private static List<double> GetLogReturns(IReadOnlyList<Candle> candles)
    {
        var returns = new List<double>(candles.Count);
        for (int i = 1; i < candles.Count; i++)
        {
            double prevClose = (double)candles[i - 1].Close;
            double currClose = (double)candles[i].Close;
            if (prevClose > 0)
            {
                returns.Add(Math.Log(currClose / prevClose));
            }
        }
        return returns;
    }

    private static double GetStandardDeviation(IReadOnlyList<double> returns)
    {
        double avgReturn = returns.Average();
        double variance = returns.Average(r => (r - avgReturn) * (r - avgReturn));
        return Math.Sqrt(variance);
    }

    private static int[] DiscretizeReturns(IReadOnlyList<double> returns, double stdDev)
    {
        var counts = new int[5];
        foreach (var r in returns)
        {
            if (r < -1.5 * stdDev) counts[0]++;
            else if (r <= -0.5 * stdDev) counts[1]++;
            else if (r < 0.5 * stdDev) counts[2]++;
            else if (r <= 1.5 * stdDev) counts[3]++;
            else counts[4]++;
        }
        return counts;
    }

    private static double CalculateEntropyFromBins(int[] counts, int totalCount)
    {
        double entropy = 0.0;
        foreach (int count in counts)
        {
            if (count > 0)
            {
                double p = (double)count / totalCount;
                entropy -= p * Math.Log2(p);
            }
        }
        return entropy;
    }

    /// <summary>
    /// Computes forecasted period volatility using a GARCH(1,1) process.
    /// Captures volatility clustering which ATR often lags.
    /// \sigma^2_t = \omega + \alpha \cdot r_{t-1}^2 + \beta \cdot \sigma^2_{t-1}
    /// </summary>
    private static double ComputeGarchVolatility(IReadOnlyList<Candle> candles, MarketRegimeDetectionOptions? options = null)
    {
        if (candles.Count < 2) return 0;

        var opts = options ?? new MarketRegimeDetectionOptions();

        // High-frequency GARCH(1,1) parameters (fast reacting)
        double omega = opts.GarchOmega;
        double alpha = opts.GarchAlpha;
        double beta = opts.GarchBeta;

        var returns = new List<double>(candles.Count);
        for (int i = 1; i < candles.Count; i++)
        {
            double prev = (double)candles[i - 1].Close;
            double curr = (double)candles[i].Close;
            if (prev > 0)
            {
                returns.Add((curr - prev) / prev);
            }
        }

        if (returns.Count == 0) return 0;

        // Initialize variance
        double variance = returns.Average(r => r * r);

        // Iteratively compute GARCH variance
        foreach (double r in returns)
        {
            variance = omega + alpha * (r * r) + beta * variance;
        }

        // Return forecasted volatility percentage for the next step
        return Math.Sqrt(variance) * 100.0;
    }
}
