using System;
using System.Collections.Generic;
using FluentAssertions;
using ThatsMyQuant.Application.Risk;
using Xunit;

namespace ThatsMyQuant.Tests.Risk;

public class PortfolioRiskEngineTests
{
    private readonly PortfolioRiskEngine _engine = new();

    [Fact]
    public void CalculateHistoricalVaR_ShouldReturn5thPercentile()
    {
        // 100 returns from -50 to 49
        var returns = new List<decimal>();
        for (int i = -50; i < 50; i++)
        {
            returns.Add(i / 100m); // -0.50 to 0.49
        }

        // 95% Confidence -> 5th percentile. 
        // 5% of 100 = 5. Index 4.
        // Sorted: -0.50, -0.49, -0.48, -0.47, -0.46
        var var95 = _engine.CalculateHistoricalVaR(returns, 0.95m);

        var95.Should().Be(-0.46m);
    }

    [Fact]
    public void CalculateCVaR_ShouldAverageTailLosses()
    {
        var returns = new List<decimal>();
        for (int i = -50; i < 50; i++)
        {
            returns.Add(i / 100m); 
        }

        // VaR is -0.46. 
        // Tail losses: -0.50, -0.49, -0.48, -0.47, -0.46
        // Average of tail: (-0.50 + -0.49 + -0.48 + -0.47 + -0.46) / 5 = -2.40 / 5 = -0.48m
        var cvar95 = _engine.CalculateCVaR(returns, 0.95m);

        cvar95.Should().Be(-0.48m);
    }

    [Fact]
    public void CalculateParametricVaR_ShouldCalculateStandardNormal()
    {
        // Simple distinct symmetric distribution: Mean = 0
        var returns = new List<decimal> { -0.10m, -0.05m, 0.0m, 0.05m, 0.10m };
        
        // Variance = (0.01 + 0.0025 + 0 + 0.0025 + 0.01) / 4 = 0.025 / 4 = 0.00625
        // StdDev = sqrt(0.00625) ~ 0.079056
        // Mean = 0
        // VaR(95%) = Mean - 1.645 * StdDev = 0 - 1.645 * 0.079056 = -0.1300m
        
        var pVar = _engine.CalculateParametricVaR(returns, 1.645m);
        
        pVar.Should().BeApproximately(-0.13005m, 0.0001m);
    }
}
