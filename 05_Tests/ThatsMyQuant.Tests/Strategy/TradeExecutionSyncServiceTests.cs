using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class TradeExecutionSyncServiceTests
{
    [Fact]
    public async Task ReconcileOpenOrdersAsync_ShouldUpdateMatchedTradeFromVenueSnapshot()
    {
        var tradeRepository = Substitute.For<ITradeRepository>();
        var logger = Substitute.For<ILogger<TradeExecutionSyncService>>();
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            RequestedQuantity = 2m,
            RemainingQuantity = 2m,
            Status = TradeStatus.Pending,
            LifecycleStatus = TradeLifecycleStatus.Accepted
        };

        tradeRepository.GetOpenAsync(Arg.Any<int>()).Returns([trade]);
        var sut = new TradeExecutionSyncService(tradeRepository, logger);

        var result = await sut.ReconcileOpenOrdersAsync(
        [
            new OpenOrderSnapshot(
                "BTCUSDT",
                TradeSide.Buy,
                ExecutionOrderIdentity.CreateClientOrderId(trade.SignalId),
                "12345",
                2m,
                0.75m,
                1.25m,
                100.4m,
                100.6m,
                "PARTIALLY_FILLED",
                DateTime.UtcNow)
        ]);

        result.MatchedTrades.Should().Be(1);
        result.UpdatedTrades.Should().Be(1);
        result.OrphanedVenueOrders.Should().Be(0);
        await tradeRepository.Received(1).UpdateAsync(Arg.Is<Trade>(tracked =>
            tracked.SignalId == trade.SignalId &&
            tracked.Status == TradeStatus.Executed &&
            tracked.LifecycleStatus == TradeLifecycleStatus.PartiallyFilled &&
            tracked.FilledQuantity == 0.75m &&
            tracked.Quantity == 0.75m &&
            tracked.RemainingQuantity == 1.25m &&
            tracked.EntryPrice == 100.6m));
    }

    [Fact]
    public async Task ReconcileOpenOrdersAsync_ShouldReportOrphanedVenueOrder_WhenLocalTradeIsMissing()
    {
        var tradeRepository = Substitute.For<ITradeRepository>();
        var logger = Substitute.For<ILogger<TradeExecutionSyncService>>();
        tradeRepository.GetOpenAsync(Arg.Any<int>()).Returns(Array.Empty<Trade>());
        var sut = new TradeExecutionSyncService(tradeRepository, logger);

        var result = await sut.ReconcileOpenOrdersAsync(
        [
            new OpenOrderSnapshot(
                "ETHUSDT",
                TradeSide.Buy,
                Guid.NewGuid().ToString("N"),
                "98765",
                1m,
                0m,
                1m,
                2500m,
                2500m,
                "NEW",
                DateTime.UtcNow)
        ]);

        result.OrphanedVenueOrders.Should().Be(1);
        result.MatchedTrades.Should().Be(0);
        await tradeRepository.DidNotReceive().UpdateAsync(Arg.Any<Trade>());
    }

    [Fact]
    public async Task ApplyExecutionReportAsync_ShouldPromoteAcceptedTradeToActive_OnFullFill()
    {
        var tradeRepository = Substitute.For<ITradeRepository>();
        var logger = Substitute.For<ILogger<TradeExecutionSyncService>>();
        var signalId = Guid.NewGuid();
        tradeRepository.GetBySignalIdAsync(signalId).Returns(new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signalId,
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            RequestedQuantity = 2m,
            RemainingQuantity = 2m,
            Status = TradeStatus.Pending,
            LifecycleStatus = TradeLifecycleStatus.Accepted
        });

        var sut = new TradeExecutionSyncService(tradeRepository, logger);

        var applied = await sut.ApplyExecutionReportAsync(new ExecutionReport(
            "BTCUSDT",
            ExecutionOrderIdentity.CreateClientOrderId(signalId),
            "12345",
            2m,
            0m,
            101.2m,
            101.2m,
            2m,
            "FILLED",
            22,
            DateTime.UtcNow));

        applied.Should().BeTrue();
        await tradeRepository.Received(1).UpdateAsync(Arg.Is<Trade>(trade =>
            trade.SignalId == signalId &&
            trade.Status == TradeStatus.Executed &&
            trade.LifecycleStatus == TradeLifecycleStatus.Active &&
            trade.FilledQuantity == 2m &&
            trade.RemainingQuantity == 0m &&
            trade.EntryPrice == 101.2m));
    }
}
