using System;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Execution cost models based on institutional market impact equations.
/// Overcomes the naive static slippage assumptions by explicitly modeling
/// the non-linear cost of clearing deep liquidity out of the central limit order book.
/// </summary>
public static class ExecutionImpactModel
{
    private const decimal BaselineDailyVolume = 100_000m; // Default aggregate baseline if unspecified
    private const decimal BaselineDailyVolatility = 0.02m; // 2% daily move
    private const decimal ImpactCoefficient = 0.15m; // Institutional fixed scaling factor for market resilience

    /// <summary>
    /// Calculates slippage penalty based on the Square-Root Law of Market Impact.
    /// Slippage = c * \sigma * \sqrt{Q / V}
    /// Matches empirical execution costs much better than flat percentage rules.
    /// </summary>
    /// <param name="referencePrice">The mid-price or reference starting price.</param>
    /// <param name="side">Trade direction.</param>
    /// <param name="quantity">Quantity intended to execute.</param>
    /// <param name="dailyVolume">Average daily volume (in base asset).</param>
    /// <param name="dailyVolatilityPercent">Daily volatility (e.g., 0.02 for 2%).</param>
    /// <returns>The final estimated execution fill price penalized by impact.</returns>
    public static decimal CalculateSquareRootImpactPrice(
        decimal referencePrice, 
        TradeSide side, 
        decimal quantity, 
        decimal dailyVolume = BaselineDailyVolume, 
        decimal dailyVolatilityPercent = BaselineDailyVolatility)
    {
        if (quantity <= 0 || dailyVolume <= 0) return referencePrice;

        // V cannot be less than Q for the model, clip at fully absorbing the whole day's liquidity
        decimal effectiveVolume = Math.Max(dailyVolume, quantity);

        // Non-linear impact magnitude
        double impactFraction = (double)ImpactCoefficient * (double)dailyVolatilityPercent * Math.Sqrt((double)(quantity / effectiveVolume));
        
        // Convert fraction into absolute price slippage
        decimal expectedSlippage = referencePrice * (decimal)impactFraction;

        return side == TradeSide.Buy 
            ? referencePrice + expectedSlippage 
            : referencePrice - expectedSlippage;
    }
}
