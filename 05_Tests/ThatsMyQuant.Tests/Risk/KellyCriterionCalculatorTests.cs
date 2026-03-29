using FluentAssertions;
using ThatsMyQuant.Application.Risk;
using Xunit;

namespace ThatsMyQuant.Tests.Risk;

public class KellyCriterionCalculatorTests
{
    [Fact]
    public void CalculateFractionalKelly_BaseOverload_ShouldCalculateCorrectly()
    {
        // WinRate = 0.5, AvgWin = 200, AvgLoss = 100
        // R = 2
        // Full Kelly = 0.5 - (0.5 / 2) = 0.5 - 0.25 = 0.25
        // Half Kelly = 0.125
        
        var output = KellyCriterionCalculator.CalculateFractionalKelly(0.5m, 200m, 100m, 0.5m);
        
        output.Should().Be(0.125m);
    }

    [Fact]
    public void CalculateFractionalKelly_ProfitFactorOverload_ShouldCalculateCorrectly()
    {
        // WinRate = 0.5
        // PF = 2.0
        // Full Kelly = W * (1 - 1/PF) = 0.5 * (1 - 0.5) = 0.25
        // Half Kelly = 0.125
        
        var output = KellyCriterionCalculator.CalculateFractionalKelly(0.5m, 2m, 0.5m);
        
        output.Should().Be(0.125m);
    }

    [Fact]
    public void CalculateFractionalKelly_NegativeEdge_ShouldReturnZero()
    {
        // WinRate = 0.4, PF = 0.8 (Losing strategy)
        var output = KellyCriterionCalculator.CalculateFractionalKelly(0.4m, 0.8m, 1.0m);
        output.Should().Be(0m);
    }

    [Fact]
    public void CalculateFractionalKelly_InfinityProfitFactor_ShouldSaturate()
    {
        // WinRate = 1.0, PF = Infinity (No losses)
        // Full Kelly = 1.0
        // Half Kelly = 0.5
        var output = KellyCriterionCalculator.CalculateFractionalKelly(1.0m, 0m, 0m, 0.5m); // AvgLoss = 0
        output.Should().Be(0.5m);
    }
}
