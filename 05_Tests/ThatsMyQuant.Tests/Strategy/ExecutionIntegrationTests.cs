using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

/// <summary>
/// Integration-style tests verifying end-to-end signal→execution using MockBrokerService.
/// </summary>
public sealed class ExecutionIntegrationTests
{
    private readonly ILogger<MockBrokerService> _logger = Substitute.For<ILogger<MockBrokerService>>();

    // ─── MockBrokerService execution ─────────────────────────────────────

    [Fact]
    public async Task MockBroker_ExecutesOrder_ReturnsTradeWithCorrectSymbol()
    {
        var broker = new MockBrokerService(_logger);
        var signal = CreateSignal("BTCUSDT", 60000m, TradeSide.Buy);

        var trade = await broker.ExecuteOrderAsync(signal);

        trade.Symbol.Should().Be("BTCUSDT");
        trade.EntryPrice.Should().Be(60000m);
        trade.Id.Should().NotBeEmpty();
        trade.SignalId.Should().Be(signal.Id);
    }

    [Fact]
    public async Task MockBroker_UsesSignalQuantity_WhenPositive()
    {
        var broker = new MockBrokerService(_logger);
        var signal = CreateSignal("ETHUSDT", 3000m, TradeSide.Buy);
        signal.Quantity = 2.5m;

        var trade = await broker.ExecuteOrderAsync(signal);

        trade.Quantity.Should().Be(2.5m);
    }

    [Fact]
    public async Task MockBroker_FallsBackToOne_WhenQuantityZero()
    {
        var broker = new MockBrokerService(_logger);
        var signal = CreateSignal("ETHUSDT", 3000m, TradeSide.Buy);
        signal.Quantity = 0m;

        var trade = await broker.ExecuteOrderAsync(signal);

        trade.Quantity.Should().Be(1.0m);
    }

    [Fact]
    public async Task MockBroker_FallsBackToOne_WhenQuantityNegative()
    {
        var broker = new MockBrokerService(_logger);
        var signal = CreateSignal("ETHUSDT", 3000m, TradeSide.Buy);
        signal.Quantity = -5m;

        var trade = await broker.ExecuteOrderAsync(signal);

        trade.Quantity.Should().Be(1.0m);
    }

    // ─── Account status ──────────────────────────────────────────────────

    [Fact]
    public async Task MockBroker_ReturnsDefaultAccount()
    {
        var broker = new MockBrokerService(_logger);
        var account = await broker.GetAccountStatusAsync();

        account.Balance.Should().Be(10000m);
        account.Provider.Should().Be("Mock");
    }

    // ─── Multiple executions ─────────────────────────────────────────────

    [Fact]
    public async Task MockBroker_GeneratesUniqueTradeIds()
    {
        var broker = new MockBrokerService(_logger);
        var signal1 = CreateSignal("BTCUSDT", 60000m, TradeSide.Buy);
        var signal2 = CreateSignal("BTCUSDT", 60100m, TradeSide.Sell);

        var trade1 = await broker.ExecuteOrderAsync(signal1);
        var trade2 = await broker.ExecuteOrderAsync(signal2);

        trade1.Id.Should().NotBe(trade2.Id);
    }

    // ─── ReplayBrokerService ─────────────────────────────────────────────

    [Fact]
    public async Task ReplayBroker_UsesSignalQuantity_WhenPositive()
    {
        var replayLogger = Substitute.For<ILogger<ReplayBrokerService>>();
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);

        var broker = new ReplayBrokerService(replayLogger, clock);
        var signal = CreateSignal("BTCUSDT", 60000m, TradeSide.Buy);
        signal.Quantity = 3.0m;

        var trade = await broker.ExecuteOrderAsync(signal);

        trade.Quantity.Should().Be(3.0m);
        trade.Status.Should().Be(TradeStatus.Executed);
    }

    [Fact]
    public async Task ReplayBroker_SetsExecutedStatus()
    {
        var replayLogger = Substitute.For<ILogger<ReplayBrokerService>>();
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        var broker = new ReplayBrokerService(replayLogger, clock);
        var signal = CreateSignal("ETHUSDT", 3000m, TradeSide.Sell);

        var trade = await broker.ExecuteOrderAsync(signal);

        trade.Status.Should().Be(TradeStatus.Executed);
        trade.Side.Should().Be(TradeSide.Sell);
        trade.EntryTime.Should().Be(new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static Signal CreateSignal(string symbol, decimal price, TradeSide side) => new()
    {
        Symbol = symbol,
        Price = price,
        Side = side,
        Confidence = 70,
        Timestamp = DateTime.UtcNow
    };
}
