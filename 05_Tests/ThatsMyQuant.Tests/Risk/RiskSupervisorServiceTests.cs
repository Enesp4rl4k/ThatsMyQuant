using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Risk;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Risk;

public class RiskSupervisorServiceTests : IAsyncDisposable
{
    private readonly ILogger<RiskSupervisorService> _logger;
    private readonly IExchangeService _exchange;
    private readonly MemoryPortfolioManager _portfolio;

    public RiskSupervisorServiceTests()
    {
        _logger = Substitute.For<ILogger<RiskSupervisorService>>();
        _exchange = Substitute.For<IExchangeService>();
        _portfolio = new MemoryPortfolioManager();
        _portfolio.SetBalance("USDT", 50_000m);
    }

    [Fact]
    public async Task ValidateOrderAsync_ShouldAccept_WhenFundsAreSufficient()
    {
        await using var sut = new RiskSupervisorService(_logger, _exchange, _portfolio);
        var signal = new Signal { Symbol = "BTCUSDT", Price = 100m, Quantity = 1m };

        var result = await sut.ValidateOrderAsync(signal);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateOrderAsync_ShouldReject_WhenFundsInsufficient()
    {
        _portfolio.SetBalance("USDT", 10m);
        await using var sut = new RiskSupervisorService(_logger, _exchange, _portfolio);
        var signal = new Signal { Symbol = "BTCUSDT", Price = 100m, Quantity = 1m };

        var result = await sut.ValidateOrderAsync(signal);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateOrderAsync_ShouldReject_WhenSystemIsHalted()
    {
        await using var sut = new RiskSupervisorService(_logger, _exchange, _portfolio);
        await sut.TriggerEmergencyHaltAsync("Test halt");

        var signal = new Signal { Symbol = "BTCUSDT", Price = 100m, Quantity = 1m };
        var result = await sut.ValidateOrderAsync(signal);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateOrderAsync_ShouldTriggerHalt_WhenRateLimitExceeded()
    {
        await using var sut = new RiskSupervisorService(_logger, _exchange, _portfolio);
        var signal = new Signal { Symbol = "BTCUSDT", Price = 1m, Quantity = 1m };

        // Send 11 orders rapidly (limit is 10/sec)
        bool lastResult = true;
        for (int i = 0; i < 11; i++)
        {
            lastResult = await sut.ValidateOrderAsync(signal);
        }

        lastResult.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenCalledMultipleTimes()
    {
        var sut = new RiskSupervisorService(_logger, _exchange, _portfolio);

        var act = async () =>
        {
            await sut.DisposeAsync();
            await sut.DisposeAsync();
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_ShouldNotThrow_WhenCalledMultipleTimes()
    {
        var sut = new RiskSupervisorService(_logger, _exchange, _portfolio);

        var act = () =>
        {
            sut.Dispose();
            sut.Dispose();
        };

        act.Should().NotThrow();
    }

    public async ValueTask DisposeAsync()
    {
        // No shared state to clean up — each test creates its own SUT
        await Task.CompletedTask;
    }
}
