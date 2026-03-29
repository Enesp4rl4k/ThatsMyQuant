using System;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Risk;

/// <summary>
/// Calculates advanced portfolio-level risk metrics including Historical VaR, Parametric VaR, and Conditional VaR (Expected Shortfall).
/// </summary>
public class PortfolioRiskEngine
{
    /// <summary>
    /// Calculates Historical Value at Risk (VaR) at a given confidence level.
    /// Represents the minimum expected loss in the worst (1 - confidence_level) cases.
    /// </summary>
    /// <param name="returns">List of historical percentage returns.</param>
    /// <param name="confidenceLevel">Confidence level (e.g., 0.95 for 95% confidence).</param>
    /// <returns>VaR threshold return (typically negative).</returns>
    public decimal CalculateHistoricalVaR(IReadOnlyList<decimal> returns, decimal confidenceLevel = 0.95m)
    {
        if (returns == null || returns.Count == 0) return 0m;
        
        var sortedReturns = returns.OrderBy(r => r).ToList();
        
        // Find the percentile index
        decimal alpha = 1.0m - confidenceLevel;
        int index = (int)Math.Max(0, Math.Ceiling(returns.Count * alpha) - 1);
        
        return sortedReturns[index];
    }

    /// <summary>
    /// Calculates Parametric Value at Risk (VaR) assuming a normal distribution.
    /// VaR = mean - (Z * standard_deviation)
    /// </summary>
    public decimal CalculateParametricVaR(IReadOnlyList<decimal> returns, decimal zScore = 1.645m) // 1.645 corresponds to 95%
    {
        if (returns == null || returns.Count < 2) return 0m;

        decimal mean = returns.Average();
        decimal variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        decimal stdDev = (decimal)Math.Sqrt((double)variance);

        return mean - (zScore * stdDev);
    }

    /// <summary>
    /// Calculates Conditional Value at Risk (CVaR) / Expected Shortfall.
    /// It is the average of all returns that are worse than the Historical VaR threshold.
    /// </summary>
    public decimal CalculateCVaR(IReadOnlyList<decimal> returns, decimal confidenceLevel = 0.95m)
    {
        if (returns == null || returns.Count == 0) return 0m;

        decimal varThreshold = CalculateHistoricalVaR(returns, confidenceLevel);
        var tailLosses = returns.Where(r => r <= varThreshold).ToList();

        if (tailLosses.Count == 0) return varThreshold;

        return tailLosses.Average();
    }
}
