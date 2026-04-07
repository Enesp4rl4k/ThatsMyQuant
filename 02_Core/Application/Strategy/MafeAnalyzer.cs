using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Maximum Adverse Excursion (MAE) and Maximum Favorable Excursion (MFE) analyzer.
/// Measures the maximum intra-trade drawdown and run-up to optimize stop-loss and
/// take-profit placement with statistical precision.
/// </summary>
public static class MafeAnalyzer
{
    public sealed record MafeResult
    {
        public decimal MaxAdverseExcursion { get; init; }
        public decimal MaxAdverseExcursionPercent { get; init; }
        public decimal MaxFavorableExcursion { get; init; }
        public decimal MaxFavorableExcursionPercent { get; init; }
        public decimal ExcursionEfficiency { get; init; }
    }

    public sealed record MafeDistribution
    {
        public int SampleCount { get; init; }
        public decimal MaeMedian { get; init; }
        public decimal MaeP75 { get; init; }
        public decimal MaeP90 { get; init; }
        public decimal MaeP95 { get; init; }
        public decimal MfeMedian { get; init; }
        public decimal MfeP75 { get; init; }
        public decimal MfeP90 { get; init; }
        public decimal OptimalStopLossPercent { get; init; }
        public decimal OptimalTakeProfitPercent { get; init; }
        public decimal EdgeRatio { get; init; }
    }

    /// <summary>
    /// Computes MAE/MFE for a single trade given its intra-trade candle history.
    /// </summary>
    public static MafeResult ComputeForTrade(
        decimal entryPrice,
        TradeSide side,
        IReadOnlyList<Candle> intraTradeBars)
    {
        if (intraTradeBars.Count == 0 || entryPrice <= 0)
            return new MafeResult();

        decimal maxAdverse = 0m, maxFavorable = 0m;

        foreach (var bar in intraTradeBars)
        {
            decimal adversePrice, favorablePrice;

            if (side == TradeSide.Buy)
            {
                adversePrice = bar.Low;
                favorablePrice = bar.High;
            }
            else
            {
                adversePrice = bar.High;
                favorablePrice = bar.Low;
            }

            decimal adverseMove = side == TradeSide.Buy
                ? entryPrice - adversePrice
                : adversePrice - entryPrice;

            decimal favorableMove = side == TradeSide.Buy
                ? favorablePrice - entryPrice
                : entryPrice - favorablePrice;

            if (adverseMove > maxAdverse)
                maxAdverse = adverseMove;
            if (favorableMove > maxFavorable)
                maxFavorable = favorableMove;
        }

        decimal maePercent = entryPrice > 0 ? maxAdverse / entryPrice * 100m : 0m;
        decimal mfePercent = entryPrice > 0 ? maxFavorable / entryPrice * 100m : 0m;
        decimal efficiency = maxFavorable > 0 ? maxAdverse / maxFavorable : 0m;

        return new MafeResult
        {
            MaxAdverseExcursion = maxAdverse,
            MaxAdverseExcursionPercent = maePercent,
            MaxFavorableExcursion = maxFavorable,
            MaxFavorableExcursionPercent = mfePercent,
            ExcursionEfficiency = 1m - efficiency
        };
    }

    /// <summary>
    /// Aggregates MAE/MFE across multiple trades to derive optimal stop-loss and take-profit levels.
    /// Uses percentile analysis: SL at P75 MAE (captures 75% of adverse moves), TP at P75 MFE.
    /// </summary>
    public static MafeDistribution ComputeDistribution(IReadOnlyList<MafeResult> results)
    {
        if (results.Count == 0)
            return new MafeDistribution();

        var maeValues = results
            .Select(r => r.MaxAdverseExcursionPercent)
            .OrderBy(v => v)
            .ToList();

        var mfeValues = results
            .Select(r => r.MaxFavorableExcursionPercent)
            .OrderBy(v => v)
            .ToList();

        decimal maeMedian = Percentile(maeValues, 50);
        decimal maeP75 = Percentile(maeValues, 75);
        decimal maeP90 = Percentile(maeValues, 90);
        decimal maeP95 = Percentile(maeValues, 95);
        decimal mfeMedian = Percentile(mfeValues, 50);
        decimal mfeP75 = Percentile(mfeValues, 75);
        decimal mfeP90 = Percentile(mfeValues, 90);

        // Optimal SL: P75 MAE — captures most adverse moves without being too tight
        // Optimal TP: P75 MFE — realistic profit target based on historical price movement
        decimal optimalSL = maeP75;
        decimal optimalTP = mfeP75;
        decimal edgeRatio = optimalSL > 0 ? optimalTP / optimalSL : 0m;

        return new MafeDistribution
        {
            SampleCount = results.Count,
            MaeMedian = maeMedian,
            MaeP75 = maeP75,
            MaeP90 = maeP90,
            MaeP95 = maeP95,
            MfeMedian = mfeMedian,
            MfeP75 = mfeP75,
            MfeP90 = mfeP90,
            OptimalStopLossPercent = optimalSL,
            OptimalTakeProfitPercent = optimalTP,
            EdgeRatio = edgeRatio
        };
    }

    /// <summary>
    /// Evaluates whether a proposed stop-loss and take-profit are well-calibrated against MAFE distribution.
    /// Returns adjustment recommendations.
    /// </summary>
    public static MafeAdjustment EvaluateExitLevels(
        decimal proposedStopLossPercent,
        decimal proposedTakeProfitPercent,
        MafeDistribution distribution)
    {
        if (distribution.SampleCount < 10)
            return new MafeAdjustment { HasSufficientData = false };

        bool slTooTight = proposedStopLossPercent < distribution.MaeMedian;
        bool slTooWide = proposedStopLossPercent > distribution.MaeP95;
        bool tpTooFar = proposedTakeProfitPercent > distribution.MfeP90;
        bool tpTooClose = proposedTakeProfitPercent < distribution.MfeMedian * 0.5m;

        return new MafeAdjustment
        {
            HasSufficientData = true,
            IsStopLossTooTight = slTooTight,
            IsStopLossTooWide = slTooWide,
            IsTakeProfitTooFar = tpTooFar,
            IsTakeProfitTooClose = tpTooClose,
            RecommendedStopLossPercent = distribution.OptimalStopLossPercent,
            RecommendedTakeProfitPercent = distribution.OptimalTakeProfitPercent,
            EdgeRatio = distribution.EdgeRatio
        };
    }

    public sealed record MafeAdjustment
    {
        public bool HasSufficientData { get; init; }
        public bool IsStopLossTooTight { get; init; }
        public bool IsStopLossTooWide { get; init; }
        public bool IsTakeProfitTooFar { get; init; }
        public bool IsTakeProfitTooClose { get; init; }
        public decimal RecommendedStopLossPercent { get; init; }
        public decimal RecommendedTakeProfitPercent { get; init; }
        public decimal EdgeRatio { get; init; }
    }

    private static decimal Percentile(List<decimal> sorted, int percentile)
    {
        if (sorted.Count == 0) return 0m;
        double rank = (percentile / 100.0) * (sorted.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper || upper >= sorted.Count) return sorted[lower];
        decimal weight = (decimal)(rank - lower);
        return sorted[lower] * (1m - weight) + sorted[upper] * weight;
    }
}
