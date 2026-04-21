using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services.Risk;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class KellyPositionSizerAgentTests
{
    private readonly KellyPositionSizerAgent _agent = new(Substitute.For<ILogger<KellyPositionSizerAgent>>());

    [Fact]
    public async Task EvaluateAsync_ShouldScaleDown_WhenRequestedSizeExceedsFractionalKelly()
    {
        var result = await _agent.EvaluateAsync(
            new Signal { Symbol = "BTCUSDT", Price = 100m },
            new TradeDecision
            {
                Confidence = 70,
                PositionSizePercent = 0.20m,
                StopLoss = 95m,
                TakeProfit = 110m,
                Side = TradeSide.Buy
            },
            []);

        result.Approved.Should().BeTrue();
        result.SizeMultiplier.Should().BeGreaterThan(0.60m);
        result.SizeMultiplier.Should().BeLessThan(0.75m);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldNotDivideByZero_WhenDecisionStartsAtZeroSize()
    {
        var result = await _agent.EvaluateAsync(
            new Signal { Symbol = "BTCUSDT", Price = 100m },
            new TradeDecision
            {
                Confidence = 70,
                PositionSizePercent = 0m,
                StopLoss = 95m,
                TakeProfit = 110m,
                Side = TradeSide.Buy
            },
            []);

        result.Approved.Should().BeTrue();
        result.SizeMultiplier.Should().BeInRange(0m, 1m);
    }
}
