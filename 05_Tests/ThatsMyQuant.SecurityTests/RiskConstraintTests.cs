using Xunit;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.SecurityTests;

public class RiskConstraintTests
{
    [Fact]
    public void KellyCriterion_ShouldNeverExceedMaxPositionSize()
    {
        // Arrange
        double winProbability = 0.99; // Very high win rate
        double profitFactor = 10.0;   // Massive profit factor = huge kelly fraction
        decimal maxPositionSizePercent = 15m; // Limit to 15%

        // Act
        decimal result = RiskCalculator.CalculateKellyPositionSize(winProbability, profitFactor, maxPositionSizePercent);

        // Assert
        Assert.True(result <= 0.15m, $"Position size exceeded bounds! Result: {result}");
        Assert.Equal(0.15m, result); // Expected clamp to max
    }

    [Fact]
    public void KellyCriterion_ShouldReturnZero_WhenEdgeIsNegative()
    {
        // Arrange
        // (p*b - q) / b
        // b = 0.5 (make half what u risk)
        // p = 0.4 (lose 60% of time)
        // Edge is firmly negative.
        double winProbability = 0.4; 
        double profitFactor = 0.5;   
        decimal maxPositionSizePercent = 5m;

        // Act
        decimal result = RiskCalculator.CalculateKellyPositionSize(winProbability, profitFactor, maxPositionSizePercent);

        // Assert
        Assert.Equal(0m, result);
    }

    [Fact]
    public void KellyCriterion_ShouldSafelyHandleZeroDivisions()
    {
        // Act
        decimal result1 = RiskCalculator.CalculateKellyPositionSize(0.5, 0, 10m);
        decimal result2 = RiskCalculator.CalculateKellyPositionSize(0, 2.0, 10m);

        // Assert
        Assert.Equal(0m, result1);
        Assert.Equal(0m, result2);
    }
}
