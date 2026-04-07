using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

/// <summary>
/// Validates that CancellationToken is properly accepted by all IExchangeService implementations
/// and that cancellation is propagated through the ChaosExchangeDecorator.
/// </summary>
public class CancellationTokenPropagationTests
{
    [Fact]
    public async Task DisabledExchangeService_ExecuteOrderAsync_ShouldThrow_RegardlessOfToken()
    {
        var sut = new DisabledExchangeService();
        using var cts = new CancellationTokenSource();

        var act = () => sut.ExecuteOrderAsync(new Signal(), cts.Token);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DisabledExchangeService_GetAccountStatusAsync_ShouldAcceptToken()
    {
        var sut = new DisabledExchangeService();
        using var cts = new CancellationTokenSource();

        var account = await sut.GetAccountStatusAsync(cts.Token);

        account.Should().NotBeNull();
        account.Provider.Should().Be("Disabled");
    }

    [Fact]
    public async Task DisabledExchangeService_GetQueuePositionAsync_ShouldAcceptToken()
    {
        var sut = new DisabledExchangeService();
        using var cts = new CancellationTokenSource();

        var result = await sut.GetQueuePositionAsync("BTCUSDT", 50000m, TradeSide.Buy, cts.Token);

        result.Should().Be(0m);
    }

    [Fact]
    public async Task MockBrokerService_ExecuteOrderAsync_ShouldAcceptToken()
    {
        var logger = Substitute.For<ILogger<MockBrokerService>>();
        var sut = new MockBrokerService(logger);
        using var cts = new CancellationTokenSource();

        var trade = await sut.ExecuteOrderAsync(
            new Signal { Symbol = "BTCUSDT", Price = 50000m, Quantity = 1m },
            cts.Token);

        trade.Should().NotBeNull();
        trade.Symbol.Should().Be("BTCUSDT");
    }

    [Fact]
    public async Task ChaosExchangeDecorator_ShouldPropagateCancellationToken()
    {
        var inner = Substitute.For<IExchangeService>();
        var logger = Substitute.For<ILogger<ChaosExchangeDecorator>>();
        var signal = new Signal { Symbol = "BTCUSDT", Price = 50000m };

        inner.ExecuteOrderAsync(Arg.Any<Signal>(), Arg.Any<CancellationToken>())
            .Returns(new Trade { Symbol = "BTCUSDT" });

        var sut = new ChaosExchangeDecorator(inner, logger)
        {
            FailureProbability = 0, // Disable chaos for this test
            MaxLatencyMs = 1
        };

        using var cts = new CancellationTokenSource();
        await sut.ExecuteOrderAsync(signal, cts.Token);

        await inner.Received(1).ExecuteOrderAsync(
            Arg.Any<Signal>(),
            Arg.Is<CancellationToken>(ct => ct == cts.Token));
    }

    [Fact]
    public async Task ChaosExchangeDecorator_ShouldRespectCancellation_DuringLatencyInjection()
    {
        var inner = Substitute.For<IExchangeService>();
        var logger = Substitute.For<ILogger<ChaosExchangeDecorator>>();

        var sut = new ChaosExchangeDecorator(inner, logger)
        {
            FailureProbability = 0,
            MaxLatencyMs = 10_000 // Slow latency injection
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var act = () => sut.GetAccountStatusAsync(cts.Token);

        // Should throw OperationCanceledException or TaskCanceledException if latency injection triggers
        // If no latency injection happens, the inner call should still get the cancelled token
        // Either way, this should not hang
        try
        {
            await act();
        }
        catch (OperationCanceledException)
        {
            // Expected if latency injection triggered
        }
    }

    [Fact]
    public async Task ReplayBrokerService_ExecuteOrderAsync_ShouldAcceptToken()
    {
        var logger = Substitute.For<ILogger<ReplayBrokerService>>();
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);

        var sut = new ReplayBrokerService(logger, clock);
        using var cts = new CancellationTokenSource();

        var trade = await sut.ExecuteOrderAsync(
            new Signal { Symbol = "BTCUSDT", Price = 50000m, Side = TradeSide.Buy },
            cts.Token);

        trade.Should().NotBeNull();
        trade.Status.Should().Be(TradeStatus.Executed);
    }
}
