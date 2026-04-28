using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Detects the current market regime (Trending, Ranging, or Volatile)
/// to adapt indicator parameters for better accuracy.
///
/// Classification (scale-invariant):
///   - atrRatio = avgATR(recent lookback) / avgATR(baseline)
///   - Volatile  → atrRatio > 1.6 (recent vol significantly elevated vs baseline)
///   - Trending  → entropy < 0.82 AND atrRatio < 1.3 (organized returns, stable vol)
///   - Ranging   → otherwise
///
/// The ratio-based volatility test replaces the previous absolute atrPercent
/// threshold, which was coupled to asset scale and timeframe (a 3% ATR/bar
/// threshold that works for daily equities mis-classifies every hourly crypto
/// bar as volatile). Comparing recent volatility to a rolling baseline works
/// for any asset class without retuning.
///
/// Adaptive parameter adjustments:
///   - Trending: RSI(10), EMA(15/40) — more responsive to ride trends
///   - Ranging: RSI(20), EMA(20/50) — slower to avoid whipsaws
///   - Volatile: RSI(14), EMA(20/50) — standard but with wider stops (3x ATR)
/// </summary>
public static class MarketRegimeDetector
{
    private const double VolatileRatioThreshold = 1.6;
    private const double TrendingRatioCeiling = 1.3;
    private const double TrendingEntropyCeiling = 0.82;
    private const int BaselineLookback = 100;
    // ADX > 20 = directional market (Wilder). Combined with entropy gate, blocks false-trend
    // classifications from short 2-3 bar pushes that fool pure entropy.
    // 2026-04-24: tested raising to 25 concurrently with RSI scoring fix; 3-fold WFO showed
    // trade count collapsed 80→13 (6x), all 3 folds negative OOS Sharpe. The ADX raise was
    // over-filtering — reverted to 20. Do not re-test without concrete evidence that ADX
    // 20-25 classifications are net-negative EV for trend strategies.
    private const double TrendingMinAdx = 20.0;
    private const int AdxPeriod = 14;

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

    public static RegimeResult Detect(IReadOnlyList<Candle> candles, int lookback = 20, int lagBars = 1, string timeframe = "1h")
    {
        // Lag to eliminate bar-t leakage: classify using bars that ended strictly before the
        // decision bar. Default lagBars=1 means "regime at bar t uses data through bar t-1".
        // Backtest and live are symmetric under this contract.
        int effectiveCount = Math.Max(0, candles.Count - lagBars);
        if (effectiveCount < lookback + AdxPeriod + 1)
            return new RegimeResult { Regime = MarketRegime.Unknown, AdaptedRsiPeriod = 14,
                AdaptedEmaFast = 20, AdaptedEmaSlow = 50, StopLossMultiplier = 2.0,
                TakeProfitMultiplier = 3.0, Description = "Insufficient data" };

        var laggedCandles = lagBars > 0 ? candles.Take(effectiveCount).ToList() : candles.ToList();
        var recentCandles = laggedCandles.TakeLast(lookback).ToList();
        double recentAtr = ComputeAverageATR(recentCandles);
        double avgPrice = recentCandles.Average(c => (double)c.Close);
        double atrPercent = avgPrice > 0 ? recentAtr / avgPrice * 100 : 0;

        int effectiveBaseline = Math.Min(laggedCandles.Count, BaselineLookback);
        double atrRatio = 1.0;
        if (effectiveBaseline >= lookback * 2)
        {
            var baselineCandles = laggedCandles.TakeLast(effectiveBaseline).ToList();
            double baselineAtr = ComputeAverageATR(baselineCandles);
            atrRatio = baselineAtr > 0 ? recentAtr / baselineAtr : 1.0;
        }

        double trendStrength = ComputeTrendStrength(recentCandles);
        double normalizedEntropy = ComputeShannonEntropy(recentCandles);
        // GARCH (alpha,beta) tuned per timeframe. Prior defaults (0.15, 0.80) were 5m-calibrated
        // and over-reacted on 1h. Published empirical ranges: 1m/5m fast (0.15/0.80), 15m/1h
        // medium (0.10/0.85), 4h/1d slow (0.07/0.90).
        var (alpha, beta) = ResolveGarchParams(timeframe);
        double garchVolatilityForecast = ComputeGarchVolatility(laggedCandles.TakeLast(Math.Min(laggedCandles.Count, BaselineLookback)).ToList(), alpha, beta);

        // ADX on the lagged window — second-pass gate for Trending.
        double adx = ComputeAdx(laggedCandles.TakeLast(AdxPeriod * 3).ToList(), AdxPeriod);

        bool isVolatile = atrRatio > VolatileRatioThreshold;
        bool isTrending = normalizedEntropy < TrendingEntropyCeiling
                          && atrRatio < TrendingRatioCeiling
                          && adx >= TrendingMinAdx;

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
                Description = $"VOLATILE — ATR ratio={atrRatio:F2}x (recent {atrPercent:F2}%), GARCH={garchVolatilityForecast:F2}%. Recent vol elevated vs baseline."
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
                Description = $"TRENDING — Entropy={normalizedEntropy:F2}, ATR ratio={atrRatio:F2}x. Organized structure. Riding the trend."
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
            Description = $"RANGING — Entropy={normalizedEntropy:F2}, ATR ratio={atrRatio:F2}x. High noise {trendStrength:P0} directional. Mean-reversion mode."
        };
    }

    /// <summary>
    /// [F1] Spread-aware regime classifier. For pairs trading, the SPREAD's regime —
    /// not leg A's — determines whether mean reversion is viable. This variant accepts a
    /// log-return (or arithmetic-return) series of the spread and classifies regime
    /// using vol-ratio + entropy. ADX is omitted (no OHLC available on a residual series).
    ///
    /// The volatility-ratio is recent-window vol / baseline-window vol, mirroring the
    /// candle-based detector's atrRatio. Entropy uses the same Shannon-on-discretized-
    /// returns method as the candle path.
    /// </summary>
    public static RegimeResult ClassifyFromReturns(IReadOnlyList<double> returns, int lookback = 20)
    {
        int minBars = lookback + AdxPeriod + 1; // match candle path's "Insufficient data" threshold
        if (returns.Count < minBars)
            return new RegimeResult
            {
                Regime = MarketRegime.Unknown,
                AdaptedRsiPeriod = 14,
                AdaptedEmaFast = 20,
                AdaptedEmaSlow = 50,
                StopLossMultiplier = 2.0,
                TakeProfitMultiplier = 3.0,
                Description = "Insufficient data"
            };

        var recent = returns.Skip(Math.Max(0, returns.Count - lookback)).ToList();
        double recentVol = GetStandardDeviation(recent);

        int baselineWin = Math.Min(returns.Count, BaselineLookback);
        double volRatio = 1.0;
        if (baselineWin >= lookback * 2)
        {
            var baseline = returns.Skip(Math.Max(0, returns.Count - baselineWin)).ToList();
            double baselineVol = GetStandardDeviation(baseline);
            volRatio = baselineVol > 0 ? recentVol / baselineVol : 1.0;
        }

        double normalizedEntropy = 1.0;
        if (Math.Abs(recentVol) >= 1e-12)
        {
            var counts = DiscretizeReturns(recent, recentVol);
            double entropy = CalculateEntropyFromBins(counts, recent.Count);
            normalizedEntropy = entropy / Math.Log2(5);
        }
        else
        {
            normalizedEntropy = 0.0; // flat spread → ordered → counts as "trending" by entropy
        }

        bool isVolatile = volRatio > VolatileRatioThreshold;
        bool isTrending = normalizedEntropy < TrendingEntropyCeiling
                          && volRatio < TrendingRatioCeiling;

        if (isVolatile)
        {
            return new RegimeResult
            {
                Regime = MarketRegime.Volatile,
                AdaptedRsiPeriod = 14,
                AdaptedEmaFast = 20,
                AdaptedEmaSlow = 50,
                StopLossMultiplier = 3.0,
                TakeProfitMultiplier = 4.5,
                Description = $"VOLATILE (spread) — vol ratio={volRatio:F2}x, entropy={normalizedEntropy:F2}."
            };
        }

        if (isTrending)
        {
            return new RegimeResult
            {
                Regime = MarketRegime.Trending,
                AdaptedRsiPeriod = 10,
                AdaptedEmaFast = 15,
                AdaptedEmaSlow = 40,
                StopLossMultiplier = 2.0,
                TakeProfitMultiplier = 4.0,
                Description = $"TRENDING (spread) — entropy={normalizedEntropy:F2}, vol ratio={volRatio:F2}x."
            };
        }

        return new RegimeResult
        {
            Regime = MarketRegime.Ranging,
            AdaptedRsiPeriod = 20,
            AdaptedEmaFast = 20,
            AdaptedEmaSlow = 50,
            StopLossMultiplier = 1.5,
            TakeProfitMultiplier = 2.0,
            Description = $"RANGING (spread) — entropy={normalizedEntropy:F2}, vol ratio={volRatio:F2}x. Mean-reversion viable."
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
    private static (double alpha, double beta) ResolveGarchParams(string timeframe) => timeframe switch
    {
        "1m" or "5m" => (0.15, 0.80),
        "15m" or "1h" => (0.10, 0.85),
        "4h" or "1d" => (0.07, 0.90),
        _ => (0.10, 0.85)
    };

    // Wilder's ADX on a sliding window. Returns the last smoothed ADX value; 0 when
    // insufficient bars. Uses +DI / -DI / DX chain then smooths DX with a Wilder RMA.
    private static double ComputeAdx(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period * 2 + 1) return 0.0;
        double plusDm = 0, minusDm = 0, tr = 0;
        // Seed with first period
        for (int i = 1; i <= period; i++)
        {
            var up = (double)(candles[i].High - candles[i - 1].High);
            var dn = (double)(candles[i - 1].Low - candles[i].Low);
            plusDm += up > dn && up > 0 ? up : 0;
            minusDm += dn > up && dn > 0 ? dn : 0;
            double hl = (double)(candles[i].High - candles[i].Low);
            double hc = Math.Abs((double)(candles[i].High - candles[i - 1].Close));
            double lc = Math.Abs((double)(candles[i].Low - candles[i - 1].Close));
            tr += Math.Max(hl, Math.Max(hc, lc));
        }
        double adx = 0;
        int dxCount = 0;
        double dxSum = 0;
        for (int i = period + 1; i < candles.Count; i++)
        {
            var up = (double)(candles[i].High - candles[i - 1].High);
            var dn = (double)(candles[i - 1].Low - candles[i].Low);
            double curPlus = up > dn && up > 0 ? up : 0;
            double curMinus = dn > up && dn > 0 ? dn : 0;
            double hl = (double)(candles[i].High - candles[i].Low);
            double hc = Math.Abs((double)(candles[i].High - candles[i - 1].Close));
            double lc = Math.Abs((double)(candles[i].Low - candles[i - 1].Close));
            double curTr = Math.Max(hl, Math.Max(hc, lc));
            plusDm = plusDm - (plusDm / period) + curPlus;
            minusDm = minusDm - (minusDm / period) + curMinus;
            tr = tr - (tr / period) + curTr;
            if (tr <= 0) continue;
            double plusDi = 100.0 * plusDm / tr;
            double minusDi = 100.0 * minusDm / tr;
            double diSum = plusDi + minusDi;
            double dx = diSum > 0 ? 100.0 * Math.Abs(plusDi - minusDi) / diSum : 0;
            if (dxCount < period)
            {
                dxSum += dx;
                dxCount++;
                if (dxCount == period) adx = dxSum / period;
            }
            else
            {
                adx = (adx * (period - 1) + dx) / period;
            }
        }
        return adx;
    }

    private static double ComputeGarchVolatility(IReadOnlyList<Candle> candles, double alpha = 0.10, double beta = 0.85)
    {
        if (candles.Count < 2) return 0;

        double omega = 0.000005;

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
