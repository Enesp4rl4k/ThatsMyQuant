using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class PositionSupervisorServiceTests
{
    private readonly PositionSupervisorService _service = new();

    [Fact]
    public void CreateSubmittedTrade_ShouldMapLifecycleAndExitControlsFromSignal()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 2m,
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending"
        };
        signal.Metadata["ExecutionMode"] = "REAL";
        signal.Metadata["PromotionStage"] = "LiveProbe";
        signal.Metadata["StopLoss"] = "97.5";
        signal.Metadata["TakeProfit"] = "108.0";
        signal.Metadata["PartialTakeProfit"] = "104.0";
        signal.Metadata["PartialExitFraction"] = "0.40";
        signal.Metadata["BreakevenTriggerFraction"] = "0.55";
        signal.Metadata["MaxHoldingBars"] = "18";
        signal.Metadata["ExitPolicyTag"] = "TrendRunner";
        signal.Metadata["RequestedPositionFraction"] = "0.03";

        var trade = _service.CreateSubmittedTrade(signal);

        trade.Status.Should().Be(TradeStatus.Pending);
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Submitted);
        trade.RequestedQuantity.Should().Be(2m);
        trade.RemainingQuantity.Should().Be(2m);
        trade.StopLoss.Should().Be(97.5m);
        trade.TakeProfit.Should().Be(108.0m);
        trade.PartialTakeProfit.Should().Be(104.0m);
        trade.PartialExitFraction.Should().Be(0.40m);
        trade.BreakevenTriggerFraction.Should().Be(0.55m);
        trade.MaxHoldingBars.Should().Be(18);
    }

    [Fact]
    public void ApplyExecutionFill_ShouldPromoteTradeIntoActiveLifecycle()
    {
        var tracked = _service.CreateSubmittedTrade(new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "ETHUSDT",
            Side = TradeSide.Buy,
            Price = 100m,
            Quantity = 1.25m
        });
        var venueTrade = new Trade
        {
            SignalId = tracked.SignalId,
            Symbol = tracked.Symbol,
            Side = tracked.Side,
            EntryPrice = 101m,
            Quantity = 1.25m,
            Status = TradeStatus.Executed,
            EntryTime = DateTime.UtcNow
        };

        _service.ApplyExecutionFill(tracked, venueTrade);

        tracked.Status.Should().Be(TradeStatus.Executed);
        tracked.LifecycleStatus.Should().Be(TradeLifecycleStatus.Active);
        tracked.Quantity.Should().Be(1.25m);
        tracked.RemainingQuantity.Should().Be(1.25m);
        tracked.FilledQuantity.Should().Be(1.25m);
    }

    [Fact]
    public void AdvanceTrade_ShouldEmitPartialExitBeforeFinalClose()
    {
        var trade = new Trade
        {
            Symbol = "THYAO",
            Side = TradeSide.Buy,
            EntryPrice = 100m,
            Quantity = 1m,
            RemainingQuantity = 1m,
            TakeProfit = 110m,
            StopLoss = 97m,
            PartialTakeProfit = 104m,
            PartialExitFraction = 0.40m,
            BreakevenTriggerFraction = 0.55m,
            ExitPolicyTag = "TrendRunner",
            LifecycleStatus = TradeLifecycleStatus.Active,
            Status = TradeStatus.Executed
        };

        var partialAction = _service.AdvanceTrade(trade, CreateCandle(104.5m, 105m, 103.8m), CreateCandle(103m, 103.5m, 102.8m), 2m);

        partialAction.Should().NotBeNull();
        partialAction!.ActionType.Should().Be(PositionLifecycleActionType.PartialExit);
        partialAction.Reason.Should().Be("PartialTakeProfit");

        _service.ApplyExitFill(trade, 104.5m, partialAction.Quantity, partialAction.Reason, DateTime.UtcNow);

        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.PartialExit);
        trade.HasTakenPartialProfit = true;

        var fullExitAction = _service.AdvanceTrade(trade, CreateCandle(96.5m, 97m, 96m), CreateCandle(99m, 100m, 98m), 2m);

        fullExitAction.Should().NotBeNull();
        fullExitAction!.ActionType.Should().Be(PositionLifecycleActionType.FullExit);
        fullExitAction.Reason.Should().Be("StopLoss");
    }

    private static Candle CreateCandle(decimal close, decimal high, decimal low)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            Open = close,
            Close = close,
            High = high,
            Low = low,
            Volume = 1_000_000
        };
}
