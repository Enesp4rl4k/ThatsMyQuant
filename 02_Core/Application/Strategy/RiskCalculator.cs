namespace ThatsMyQuant.Application.Strategy;

public static class RiskCalculator
{
    /// <summary>
    /// Calculates the optimal fractional position size based on the Kelly Criterion.
    /// f* = (p*b - q) / b
    /// Ensures strictly safe outputs:
    /// 1. Will never output < 0.
    /// 2. Will never output > maxPositionSize limit.
    /// 3. Safely handles divide by zero if b = 0.
    /// </summary>
    public static decimal CalculateKellyPositionSize(double winProbability, double profitFactor, decimal maxPositionSizePercent)
    {
        if (winProbability <= 0 || profitFactor <= 0) return 0m;
        
        double p = winProbability;
        double q = 1.0 - p;
        double b = profitFactor;

        double kellyFraction = 0;
        if (b > 0)
        {
            kellyFraction = Math.Max(0, (b * p - q) / b);
        }

        decimal positionSize = Math.Min((decimal)kellyFraction, maxPositionSizePercent / 100m);
        return Math.Max(0m, positionSize); // Absolute floor clamp
    }
}
