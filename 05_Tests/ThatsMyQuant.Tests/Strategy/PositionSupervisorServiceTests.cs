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

    // --- Breakeven Protection Tests ---

    [Fact]
    public void AdvanceTrade_ShouldArmBreakeven_WhenPriceReachesTriggerFraction()
    {
        var trade = CreateActiveTrade(side: TradeSide.Buy, entryPrice: 100m, takeProfit: 110m, breakevenFraction: 0.50m);

        // Price reaches 50% of distance to TP: 100 + (110-100)*0.50 = 105
        // Candle high must be >= 105
        _service.AdvanceTrade(trade, CreateCandle(104m, 106m, 103m), CreateCandle(103m, 103.5m, 102.5m), 2m);

        trade.IsBreakevenArmed.Should().BeTrue();
        trade.StopLoss.Should().BeGreaterThan(100m); // Moved to breakeven + buffer
    }

    [Fact]
    public void AdvanceTrade_ShouldNotArmBreakeven_WhenPriceBelowTrigger()
    {
        var trade = CreateActiveTrade(side: TradeSide.Buy, entryPrice: 100m, takeProfit: 110m, breakevenFraction: 0.50m);

        _service.AdvanceTrade(trade, CreateCandle(102m, 103m, 101m), CreateCandle(101m, 101.5m, 100.5m), 2m);

        trade.IsBreakevenArmed.Should().BeFalse();
    }

    [Fact]
    public void AdvanceTrade_SellSide_ShouldArmBreakevenWhenPriceDrops()
    {
        var trade = CreateActiveTrade(side: TradeSide.Sell, entryPrice: 100m, takeProfit: 90m, breakevenFraction: 0.50m);
        trade.StopLoss = 105m;

        // 50% of distance: 100 - (100-90)*0.50 = 95. Candle low must be <= 95
        _service.AdvanceTrade(trade, CreateCandle(96m, 97m, 94m), CreateCandle(97m, 98m, 96m), 2m);

        trade.IsBreakevenArmed.Should().BeTrue();
        trade.StopLoss.Should().BeLessThan(100m); // Moved down to breakeven - buffer
    }

    // --- Exit Policy Tests ---

    [Fact]
    public void AdvanceTrade_TrendRunner_ShouldExitOnMomentumFade()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy, entryPrice: 100m, takeProfit: 120m,
            exitPolicyTag: "TrendRunner", breakevenFraction: 0.50m);
        trade.MaxHoldingBars = 20;
        trade.IsBreakevenArmed = true;
        trade.BarsHeld = 9; // >= max(6, 20/2=10)? No, 9 < 10

        // Need barsHeld >= max(6, 10) = 10 and bearish bar
        trade.BarsHeld = 10;
        var bearishCandle = new Candle
        {
            Timestamp = DateTime.UtcNow,
            Open = 108m, Close = 106m, High = 108m, Low = 105.5m, Volume = 1_000_000
        };
        var previousCandle = CreateCandle(107m, 108m, 106m);

        // Use large ATR so trailing stop distance is wide enough to not trigger
        var action = _service.AdvanceTrade(trade, bearishCandle, previousCandle, 5m);

        action.Should().NotBeNull();
        action!.ActionType.Should().Be(PositionLifecycleActionType.FullExit);
        action.Reason.Should().Be("TrendMomentumFade");
    }

    [Fact]
    public void AdvanceTrade_MeanReversion_ShouldTimeoutAfter4Bars()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy, entryPrice: 100m, takeProfit: 110m,
            exitPolicyTag: "MeanReversion");
        trade.BarsHeld = 3; // Will become 4 after AdvanceTrade increments

        var action = _service.AdvanceTrade(trade, CreateCandle(101m, 102m, 100m), CreateCandle(100.5m, 101m, 100m), 2m);

        action.Should().NotBeNull();
        action!.Reason.Should().Be("MeanReversionTimeout");
    }

    [Fact]
    public void AdvanceTrade_HftScalp_ShouldExitAfter2Bars()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy, entryPrice: 100m, takeProfit: 110m,
            exitPolicyTag: "HftScalp");
        trade.BarsHeld = 1; // Will become 2 after AdvanceTrade increments

        var action = _service.AdvanceTrade(trade, CreateCandle(101m, 102m, 100m), CreateCandle(100.5m, 101m, 100m), 2m);

        action.Should().NotBeNull();
        action!.Reason.Should().Be("HftMicroExit");
    }

    [Fact]
    public void AdvanceTrade_BreakoutExpansion_ShouldExitOnFailure()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy, entryPrice: 100m, takeProfit: 115m,
            exitPolicyTag: "BreakoutExpansion");
        trade.IsBreakevenArmed = true;
        trade.BarsHeld = 2; // Will become 3 after AdvanceTrade increments

        // Bearish bar: close < open && close < previous.close
        var bearishCandle = new Candle
        {
            Timestamp = DateTime.UtcNow,
            Open = 107m, Close = 104m, High = 107m, Low = 103.5m, Volume = 1_000_000
        };
        var previousCandle = CreateCandle(105m, 106m, 104m);

        // Use large ATR so trailing stop distance is wide enough to not trigger
        var action = _service.AdvanceTrade(trade, bearishCandle, previousCandle, 5m);

        action.Should().NotBeNull();
        action!.Reason.Should().Be("BreakoutFailure");
    }

    // --- Time Stop Tests ---

    [Fact]
    public void AdvanceTrade_ShouldExitOnTimeStop_WhenMaxHoldingBarsReached()
    {
        var trade = CreateActiveTrade(side: TradeSide.Buy, entryPrice: 100m, takeProfit: 200m);
        trade.MaxHoldingBars = 5;
        trade.BarsHeld = 4; // Will become 5 after AdvanceTrade increments

        var action = _service.AdvanceTrade(trade, CreateCandle(101m, 102m, 100m), CreateCandle(100.5m, 101m, 100m), 2m);

        action.Should().NotBeNull();
        action!.ActionType.Should().Be(PositionLifecycleActionType.FullExit);
        action.Reason.Should().Contain("TimeStop");
    }

    // --- Sell-Side Stop/TP Tests ---

    [Fact]
    public void AdvanceTrade_SellSide_ShouldTriggerStopLossWhenPriceRises()
    {
        var trade = CreateActiveTrade(side: TradeSide.Sell, entryPrice: 100m, takeProfit: 90m);
        trade.StopLoss = 105m;

        var action = _service.AdvanceTrade(trade, CreateCandle(106m, 107m, 105m), CreateCandle(103m, 104m, 102m), 2m);

        action.Should().NotBeNull();
        action!.Reason.Should().Be("StopLoss");
    }

    [Fact]
    public void AdvanceTrade_SellSide_ShouldTriggerTakeProfitWhenPriceDrops()
    {
        var trade = CreateActiveTrade(side: TradeSide.Sell, entryPrice: 100m, takeProfit: 90m);
        trade.StopLoss = 105m;

        var action = _service.AdvanceTrade(trade, CreateCandle(89m, 91m, 88m), CreateCandle(92m, 93m, 91m), 2m);

        action.Should().NotBeNull();
        action!.Reason.Should().Be("TakeProfit");
    }

    // --- ApplyExitFill Tests ---

    [Fact]
    public void ApplyExitFill_FullClose_ShouldSetClosedLifecycle()
    {
        var trade = CreateActiveTrade(side: TradeSide.Buy, entryPrice: 100m, takeProfit: 110m);
        trade.Quantity = 1m;

        _service.ApplyExitFill(trade, 108m, 1m, "TakeProfit", DateTime.UtcNow);

        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Closed);
        trade.Quantity.Should().Be(0m);
        trade.ExitPrice.Should().Be(108m);
        trade.ExitReason.Should().Be("TakeProfit");
        trade.ExitTime.Should().NotBe(default);
    }

    [Fact]
    public void ApplyExitFill_PartialClose_ShouldSetPartialExitLifecycle()
    {
        var trade = CreateActiveTrade(side: TradeSide.Buy, entryPrice: 100m, takeProfit: 110m);
        trade.Quantity = 2m;

        _service.ApplyExitFill(trade, 105m, 0.8m, "PartialTakeProfit", DateTime.UtcNow);

        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.PartialExit);
        trade.Quantity.Should().Be(1.2m);
        trade.ClosedQuantity.Should().Be(0.8m);
        trade.HasTakenPartialProfit.Should().BeTrue();
    }

    [Fact]
    public void ApplyExitFill_ZeroQuantity_ShouldBeNoOp()
    {
        var trade = CreateActiveTrade(side: TradeSide.Buy, entryPrice: 100m, takeProfit: 110m);
        trade.Quantity = 1m;
        var originalStatus = trade.LifecycleStatus;

        _service.ApplyExitFill(trade, 105m, 0m, "Test", DateTime.UtcNow);

        trade.Quantity.Should().Be(1m);
        trade.LifecycleStatus.Should().Be(originalStatus);
    }

    [Fact]
    public void ApplyExitFill_ShouldComputeWeightedAverageExitPrice()
    {
        var trade = CreateActiveTrade(side: TradeSide.Buy, entryPrice: 100m, takeProfit: 110m);
        trade.Quantity = 2m;

        _service.ApplyExitFill(trade, 105m, 1m, "PartialTakeProfit", DateTime.UtcNow);
        _service.ApplyExitFill(trade, 108m, 1m, "TakeProfit", DateTime.UtcNow);

        trade.AverageExitPrice.Should().Be(106.5m); // (105*1 + 108*1) / 2
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Closed);
    }

    // --- Partial Exit Trigger Tests ---

    [Fact]
    public void AdvanceTrade_SellSide_ShouldTriggerPartialTakeProfit()
    {
        var trade = CreateActiveTrade(side: TradeSide.Sell, entryPrice: 100m, takeProfit: 90m);
        trade.PartialTakeProfit = 95m;
        trade.PartialExitFraction = 0.30m;
        trade.Quantity = 2m;

        var action = _service.AdvanceTrade(trade, CreateCandle(94m, 95.5m, 93m), CreateCandle(96m, 97m, 95m), 2m);

        action.Should().NotBeNull();
        action!.ActionType.Should().Be(PositionLifecycleActionType.PartialExit);
        action.Reason.Should().Be("PartialTakeProfit");
        action.Quantity.Should().Be(0.6m); // 2 * 0.30
    }

    // --- MarkFailed Tests ---

    [Fact]
    public void MarkFailed_ShouldSetFailedStatusAndReason()
    {
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            Price = 100m
        };
        var trade = _service.CreateSubmittedTrade(signal);

        _service.MarkFailed(trade, "Venue rejected");

        trade.Status.Should().Be(TradeStatus.Failed);
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Failed);
        trade.ExitReason.Should().Be("Venue rejected");
    }

    // --- Helpers ---

    private static Trade CreateActiveTrade(
        TradeSide side = TradeSide.Buy,
        decimal entryPrice = 100m,
        decimal takeProfit = 110m,
        string exitPolicyTag = "TrendRunner",
        decimal breakevenFraction = 0m)
        => new()
        {
            Symbol = "BTCUSDT",
            Side = side,
            EntryPrice = entryPrice,
            Quantity = 1m,
            RemainingQuantity = 1m,
            TakeProfit = takeProfit,
            StopLoss = side == TradeSide.Buy ? entryPrice * 0.97m : entryPrice * 1.03m,
            BreakevenTriggerFraction = breakevenFraction,
            ExitPolicyTag = exitPolicyTag,
            LifecycleStatus = TradeLifecycleStatus.Active,
            Status = TradeStatus.Executed,
            OptimalPriceSinceEntry = entryPrice
        };

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
