using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class LivePositionLifecycleServiceTests
{
    [Fact]
    public async Task SuperviseOpenTradesAsync_ShouldApplyPartialExitThroughSupervisor()
    {
        var tradeRepository = Substitute.For<ITradeRepository>();
        var marketDataService = Substitute.For<IMarketDataService>();
        var exchangeService = Substitute.For<IExchangeService>();
        var logger = Substitute.For<ILogger<LivePositionLifecycleService>>();
        var supervisor = new PositionSupervisorService();

        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = Guid.NewGuid(),
            Symbol = "THYAO",
            Side = TradeSide.Buy,
            EntryPrice = 100m,
            Quantity = 10m,
            RemainingQuantity = 10m,
            FilledQuantity = 10m,
            RequestedQuantity = 10m,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active,
            StopLoss = 97m,
            TakeProfit = 110m,
            PartialTakeProfit = 104m,
            PartialExitFraction = 0.40m,
            ExitPolicyTag = "TrendRunner",
            BreakevenTriggerFraction = 0.55m,
            MaxHoldingBars = 18,
            EntryTime = DateTime.UtcNow.AddMinutes(-15)
        };

        tradeRepository.GetOpenAsync(Arg.Any<int>()).Returns([trade]);
        marketDataService.GetCandlesAsync("THYAO", 3, "BIST", "5m")
            .Returns(new List<Candle>
            {
                CreateCandle(103m, 103.5m, 102.8m, DateTime.UtcNow.AddMinutes(-10)),
                CreateCandle(104.5m, 105m, 103.8m, DateTime.UtcNow.AddMinutes(-5)),
                CreateCandle(104.5m, 105m, 103.8m, DateTime.UtcNow)
            });

        exchangeService.ExecuteOrderAsync(Arg.Any<Signal>())
            .Returns(new Trade
            {
                Symbol = "THYAO",
                Side = TradeSide.Sell,
                EntryPrice = 104.25m,
                Quantity = 4m,
                FilledQuantity = 4m,
                RequestedQuantity = 4m,
                RemainingQuantity = 4m,
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Active,
                EntryTime = DateTime.UtcNow
            });

        var service = new LivePositionLifecycleService(
            tradeRepository,
            marketDataService,
            exchangeService,
            supervisor,
            logger);

        var supervised = await service.SuperviseOpenTradesAsync(CancellationToken.None);

        supervised.Should().Be(1);
        await tradeRepository.Received().UpdateAsync(Arg.Is<Trade>(t =>
            t.Id == trade.Id &&
            t.LifecycleStatus == TradeLifecycleStatus.PartialExit &&
            t.Quantity == 6m &&
            t.ClosedQuantity == 4m &&
            t.ExitReason == "PartialTakeProfit"));
    }

    [Fact]
    public async Task SuperviseOpenTradesAsync_ShouldKeepTradeClosing_WhenVenueExitIsAcceptedWithoutFill()
    {
        var tradeRepository = Substitute.For<ITradeRepository>();
        var marketDataService = Substitute.For<IMarketDataService>();
        var exchangeService = Substitute.For<IExchangeService>();
        var logger = Substitute.For<ILogger<LivePositionLifecycleService>>();
        var supervisor = new PositionSupervisorService();

        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            EntryPrice = 100m,
            Quantity = 1m,
            RemainingQuantity = 1m,
            FilledQuantity = 1m,
            RequestedQuantity = 1m,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active,
            StopLoss = 97m,
            TakeProfit = 110m,
            EntryTime = DateTime.UtcNow.AddMinutes(-15)
        };

        tradeRepository.GetOpenAsync(Arg.Any<int>()).Returns([trade]);
        marketDataService.GetCandlesAsync("BTCUSDT", 3, "Crypto", "5m")
            .Returns(new List<Candle>
            {
                CreateCandle(99m, 100m, 98m, DateTime.UtcNow.AddMinutes(-10)),
                CreateCandle(96.5m, 97m, 96m, DateTime.UtcNow.AddMinutes(-5)),
                CreateCandle(96.5m, 97m, 96m, DateTime.UtcNow)
            });

        exchangeService.ExecuteOrderAsync(Arg.Any<Signal>())
            .Returns(new Trade
            {
                Symbol = "BTCUSDT",
                Side = TradeSide.Sell,
                EntryPrice = 96.4m,
                Quantity = 0m,
                FilledQuantity = 0m,
                RequestedQuantity = 1m,
                RemainingQuantity = 1m,
                Status = TradeStatus.Pending,
                LifecycleStatus = TradeLifecycleStatus.Accepted,
                EntryTime = DateTime.UtcNow
            });

        var service = new LivePositionLifecycleService(
            tradeRepository,
            marketDataService,
            exchangeService,
            supervisor,
            logger);

        await service.SuperviseOpenTradesAsync(CancellationToken.None);

        await tradeRepository.Received().UpdateAsync(Arg.Is<Trade>(t =>
            t.Id == trade.Id &&
            t.LifecycleStatus == TradeLifecycleStatus.Closing &&
            t.Quantity == 1m &&
            t.ExitReason == "StopLoss"));
    }

    private static Candle CreateCandle(decimal close, decimal high, decimal low, DateTime timestamp)
        => new()
        {
            Timestamp = timestamp,
            Open = close,
            Close = close,
            High = high,
            Low = low,
            Volume = 1_000_000
        };
}
