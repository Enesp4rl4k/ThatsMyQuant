using System;

namespace ThatsMyQuant.Application.Risk;

/// <summary>
/// Calculates optimal capital allocation fractions using the Kelly Criterion to maximize long-term growth while managing risk.
/// </summary>
public static class KellyCriterionCalculator
{
    /// <summary>
    /// Calculates the Fractional Kelly Criterion position sizing recommendation.
    /// K = W - ((1 - W) / R)
    /// </summary>
    /// <param name="winRate">Historical win rate (0.0 to 1.0).</param>
    /// <param name="averageWin">Magnitude of the average winning trade.</param>
    /// <param name="averageLoss">Magnitude of the average losing trade.</param>
    /// <param name="fraction">Fraction to apply (e.g., 0.5 for Half-Kelly, providing a safer institutional buffer against volatility). Default is Half-Kelly.</param>
    /// <returns>Recommended maximum allocation percentage (e.g., 0.15 = 15%).</returns>
    public static decimal CalculateFractionalKelly(decimal winRate, decimal averageWin, decimal averageLoss, decimal fraction = 0.5m)
    {
        // Edge cases
        if (winRate <= 0m) return 0m;
        if (winRate >= 1.0m) return 1.0m * fraction; 
        if (averageLoss == 0m) return winRate * fraction; // Infinite reward-to-risk implies full bet (scaled by fraction)
        if (averageWin == 0m) return 0m;

        // Ensure loss is absolute to calculate Risk/Reward accurately
        decimal absAverageLoss = Math.Abs(averageLoss);
        
        // Reward-to-Risk ratio (R)
        decimal riskRewardRatio = averageWin / absAverageLoss;

        if (riskRewardRatio == 0m) return 0m;

        // Kelly Formula: K = W - ((1 - W) / R)
        decimal fullKelly = winRate - ((1.0m - winRate) / riskRewardRatio);

        // Clamp below zero (never bet if edge is negative)
        fullKelly = Math.Max(0m, fullKelly);

        // fractional scaling for safety buffer
        decimal fractionalKelly = fullKelly * fraction;

        // Cap at 100% total portfolio allocation
        return Math.Min(1.0m, fractionalKelly);
    }

    /// <summary>
    /// Calculates the Fractional Kelly Criterion using Win Rate and Profit Factor.
    /// Mathematically derived: K = W * (1 - 1/PF)
    /// </summary>
    /// <param name="winRate">Historical win rate (0.0 to 1.0).</param>
    /// <param name="profitFactor">Profit factor (Total Gross Win / Total Gross Loss).</param>
    /// <param name="fraction">Fraction to apply (e.g., 0.5 for Half-Kelly). Default is Half-Kelly.</param>
    /// <returns>Recommended maximum allocation percentage.</returns>
    public static decimal CalculateFractionalKelly(decimal winRate, decimal profitFactor, decimal fraction = 0.5m)
    {
        if (winRate <= 0m || profitFactor <= 1.0m) return 0m;
        
        decimal fullKelly = winRate * (1.0m - (1.0m / profitFactor));
        return Math.Min(1.0m, Math.Max(0m, fullKelly * fraction));
    }
}
