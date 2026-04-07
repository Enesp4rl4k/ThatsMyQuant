using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class HftExecutionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldStampRealExecutionMetadata()
    {
        var exchangeService = Substitute.For<IExchangeService>();
        var logger = Substitute.For<ILogger<HftExecutionService>>();
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "GARAN",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 1m,
            Confidence = 82,
            Timestamp = DateTime.UtcNow
        };

        exchangeService.ExecuteOrderAsync(Arg.Any<Signal>())
            .Returns(new Trade
            {
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = 100.2m,
                Quantity = 1m,
                FilledQuantity = 1m,
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Active,
                EntryTime = DateTime.UtcNow
            });

        var sut = new HftExecutionService(exchangeService, logger);

        var trade = await sut.ExecuteAsync(signal);

        trade.ExecutionMode.Should().Be("REAL");
        trade.ExecutionLatencyMs.Should().BeGreaterThanOrEqualTo(0);
        trade.EntrySlippagePercent.Should().BeGreaterThan(0m);
        await exchangeService.Received(1).ExecuteOrderAsync(Arg.Any<Signal>());
    }

    [Fact]
    public async Task ExecuteLimitOrderAsync_ShouldPlaceLimitOrderAndTrackActiveState()
    {
        var exchangeService = Substitute.For<IExchangeService>();
        var logger = Substitute.For<ILogger<HftExecutionService>>();
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "GARAN",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 1m,
            Confidence = 50,
            OrderFlowImbalance = 0.20,
            WallAgeMs = 1000,
            Timestamp = DateTime.UtcNow
        };
        var orderBook = new OrderBookSnapshot
        {
            Symbol = "GARAN",
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Bids =
            [
                new OrderBookLevel(99.95m, 10m),
                new OrderBookLevel(99.90m, 12m)
            ],
            Asks =
            [
                new OrderBookLevel(100.05m, 8m),
                new OrderBookLevel(100.10m, 9m)
            ]
        };

        exchangeService.ExecuteOrderAsync(Arg.Any<Signal>())
            .Returns(call =>
            {
                var routedSignal = call.Arg<Signal>();
                return new Trade
                {
                    Id = Guid.NewGuid(),
                    SignalId = routedSignal.Id,
                    Symbol = routedSignal.Symbol,
                    Side = routedSignal.Side,
                    EntryPrice = routedSignal.Price,
                    Quantity = routedSignal.Quantity,
                    FilledQuantity = routedSignal.Quantity,
                    RequestedQuantity = routedSignal.Quantity,
                    RemainingQuantity = 0m,
                    Status = TradeStatus.Executed,
                    LifecycleStatus = TradeLifecycleStatus.Active,
                    EntryTime = DateTime.UtcNow
                };
            });

        var sut = new HftExecutionService(exchangeService, logger);

        var trade = await sut.ExecuteLimitOrderAsync(signal, orderBook);

        trade.ExecutionMode.Should().Be("LIMIT_MAKER");
        sut.GetActiveOrders().Should().ContainSingle(order => order.OrderId == trade.Id);
        await exchangeService.Received(1).ExecuteOrderAsync(Arg.Is<Signal>(queued =>
            queued.ExecutionAction == ExecutionActionType.LimitFrontRunBuy &&
            queued.Price < 100.05m));
    }

    [Fact]
    public async Task ExecuteLimitOrderAsync_ShouldFallbackToMarket_WhenUrgencyIsHigh()
    {
        var exchangeService = Substitute.For<IExchangeService>();
        var logger = Substitute.For<ILogger<HftExecutionService>>();
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "GARAN",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 1m,
            Confidence = 100,
            OrderFlowImbalance = 1.0,
            WallAgeMs = 5000,
            Timestamp = DateTime.UtcNow
        };

        exchangeService.ExecuteOrderAsync(Arg.Any<Signal>())
            .Returns(new Trade
            {
                Id = Guid.NewGuid(),
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.Price,
                Quantity = signal.Quantity,
                FilledQuantity = signal.Quantity,
                RequestedQuantity = signal.Quantity,
                RemainingQuantity = 0m,
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Active,
                EntryTime = DateTime.UtcNow
            });

        var sut = new HftExecutionService(exchangeService, logger);

        var trade = await sut.ExecuteLimitOrderAsync(signal);

        trade.ExecutionMode.Should().Be("REAL");
        sut.GetActiveOrders().Should().BeEmpty();
        await exchangeService.Received(1).ExecuteOrderAsync(Arg.Any<Signal>());
    }
}
