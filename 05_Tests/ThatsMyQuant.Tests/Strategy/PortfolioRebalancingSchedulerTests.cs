using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class PortfolioRebalancingSchedulerTests
{
    [Fact]
    public void BuildPlan_WhenWeightsDriftBeyondTolerance_ProducesActions()
    {
        var sut = new PortfolioRebalancingScheduler();
        MarketHoursScheduler.OverrideCanTrade = true;

        try
        {
            var result = sut.BuildPlan(
                "BIST",
                cash: 20_000m,
                positions:
                [
                    new Position { Symbol = "THYAO", Quantity = 100m, AverageEntryPrice = 100m },
                    new Position { Symbol = "GARAN", Quantity = 50m, AverageEntryPrice = 50m }
                ],
                latestPrices: new Dictionary<string, decimal>
                {
                    ["THYAO"] = 120m,
                    ["GARAN"] = 45m
                },
                targetWeights: new Dictionary<string, decimal>
                {
                    ["THYAO"] = 0.40m,
                    ["GARAN"] = 0.35m
                },
                driftTolerancePercent: 3m);

            result.CanExecuteNow.Should().BeTrue();
            result.Actions.Should().NotBeEmpty();
        }
        finally
        {
            MarketHoursScheduler.OverrideCanTrade = null;
        }
    }
}
