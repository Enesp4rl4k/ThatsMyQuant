using System;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class ExecutionImpactModelTests
{
    [Fact]
    public void CalculateSquareRootImpactPrice_SmallBuyOrder_ShouldHaveMinimalSlippage()
    {
        decimal referencePrice = 100m;
        decimal quantity = 10m;
        decimal dailyVol = 100_000m; // Huge daily volume compared to order
        decimal dailyVolatility = 0.02m;

        decimal fillPrice = ExecutionImpactModel.CalculateSquareRootImpactPrice(
            referencePrice, TradeSide.Buy, quantity, dailyVol, dailyVolatility);

        // Q/V = 10 / 100000 = 0.0001. Sqrt = 0.01. Impact = 0.15 * 0.02 * 0.01 = 0.00003
        // Slippage = 100 * 0.00003 = 0.003
        fillPrice.Should().BeApproximately(100.003m, 0.001m);
    }

    [Fact]
    public void CalculateSquareRootImpactPrice_MassiveSellOrder_ShouldHaveNonLinearSquareRootSlippage()
    {
        decimal referencePrice = 100m;
        decimal quantity = 10_000m; // 10% of daily volume
        decimal dailyVol = 100_000m;
        decimal dailyVolatility = 0.02m;

        decimal fillPrice = ExecutionImpactModel.CalculateSquareRootImpactPrice(
            referencePrice, TradeSide.Sell, quantity, dailyVol, dailyVolatility);

        // Q/V = 0.1. Sqrt = ~0.316. Impact = 0.15 * 0.02 * 0.316 = 0.000948
        // Slippage = ~0.0948
        fillPrice.Should().BeApproximately(99.905m, 0.002m);
    }
}
