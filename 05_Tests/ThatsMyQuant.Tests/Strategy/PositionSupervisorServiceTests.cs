using FluentAssertions;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class PositionSupervisorServiceTests
{
    private readonly TradingCoreOptions _options = new() { BreakevenBufferFraction = 0.0005m };
    private readonly PositionSupervisorService _sut;

    public PositionSupervisorServiceTests()
    {
        _sut = new PositionSupervisorService(Options.Create(_options));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Signal CreateSignal(
        TradeSide side = TradeSide.Buy,
        decimal price = 100m,
        decimal quantity = 10m,
        string strategyTag = "Momentum",
        string regimeTag = "Trending",
        Dictionary<string, string>? metadata = null)
    {
        return new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Side = side,
            Price = price,
            Quantity = quantity,
            StrategyTag = strategyTag,
            RegimeTag = regimeTag,
            Metadata = metadata ?? new Dictionary<string, string>()
        };
    }

    private static Trade CreateActiveTrade(
        TradeSide side = TradeSide.Buy,
        decimal entryPrice = 100m,
        decimal quantity = 10m,
        decimal stopLoss = 95m,
        decimal takeProfit = 110m,
        decimal breakevenTriggerFraction = 0.50m)
    {
        return new Trade
        {
            Symbol = "BTCUSDT",
            Side = side,
            EntryPrice = entryPrice,
            MarkPrice = entryPrice,
            Quantity = quantity,
            RemainingQuantity = quantity,
            RequestedQuantity = quantity,
            FilledQuantity = quantity,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            BreakevenTriggerFraction = breakevenTriggerFraction,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active,
            OptimalPriceSinceEntry = entryPrice,
            ExitPolicyTag = "TrendRunner"
        };
    }

    private static Candle CreateCandle(decimal close, decimal high, decimal low, decimal open = 0m)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            Open = open == 0m ? close : open,
            Close = close,
            High = high,
            Low = low,
            Volume = 1_000_000
        };

    // -----------------------------------------------------------------------
    // 1. CreateSubmittedTrade — maps all signal fields correctly
    // -----------------------------------------------------------------------

    [Fact]
    public void CreateSubmittedTrade_ShouldMapSignalToTrade_WithCorrectFields()
    {
        var metadata = new Dictionary<string, string>
        {
            { "StopLoss", "95" },
            { "TakeProfit", "110" },
            { "PartialTakeProfit", "107" },
            { "PartialExitFraction", "0.30" },
            { "BreakevenTriggerFraction", "0.60" },
            { "MaxHoldingBars", "20" },
            { "ExecutionMode", "PAPER" },
            { "PromotionStage", "Shadow" },
            { "ExitPolicyTag", "MeanReversion" },
            { "RequestedPositionFraction", "0.05" }
        };
        var signal = CreateSignal(
            side: TradeSide.Buy,
            price: 100m,
            quantity: 5m,
            strategyTag: "Momentum",
            regimeTag: "Trending",
            metadata: metadata);

        var trade = _sut.CreateSubmittedTrade(signal);

        trade.SignalId.Should().Be(signal.Id);
        trade.Symbol.Should().Be("BTCUSDT");
        trade.Side.Should().Be(TradeSide.Buy);
        trade.EntryPrice.Should().Be(100m);
        trade.MarkPrice.Should().Be(100m);
        trade.Status.Should().Be(TradeStatus.Pending);
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Submitted);
        trade.StrategyTag.Should().Be("Momentum");
        trade.RegimeTag.Should().Be("Trending");
        trade.ExitPolicyTag.Should().Be("MeanReversion");
        trade.ExecutionMode.Should().Be("PAPER");
        trade.PromotionStage.Should().Be("Shadow");
        trade.RequestedQuantity.Should().Be(5m);
        trade.RemainingQuantity.Should().Be(5m);
        trade.StopLoss.Should().Be(95m);
        trade.TakeProfit.Should().Be(110m);
        trade.PartialTakeProfit.Should().Be(107m);
        trade.PartialExitFraction.Should().Be(0.30m);
        trade.BreakevenTriggerFraction.Should().Be(0.60m);
        trade.MaxHoldingBars.Should().Be(20);
        trade.PositionSizePercent.Should().Be(0.05m);
        trade.SubmittedTime.Should().NotBeNull();
        trade.EntryTime.Should().BeAfter(DateTime.MinValue);
    }

    [Fact]
    public void CreateSubmittedTrade_ShouldUseDefaults_WhenMetadataMissing()
    {
        var signal = CreateSignal(strategyTag: "", regimeTag: "");

        var trade = _sut.CreateSubmittedTrade(signal);

        trade.StrategyTag.Should().Be("TrendConfluence");
        trade.RegimeTag.Should().Be("Unknown");
        trade.ExitPolicyTag.Should().Be("TrendRunner");
        trade.ExecutionMode.Should().Be("REAL");
        trade.PromotionStage.Should().Be("LiveProbe");
        trade.BreakevenTriggerFraction.Should().Be(0.50m);
    }

    [Fact]
    public void CreateSubmittedTrade_ShouldFallBackToMetadataQuantity_WhenSignalQuantityIsZero()
    {
        var signal = CreateSignal(quantity: 0m, metadata: new Dictionary<string, string>
        {
            { "RequestedQuantity", "7.5" }
        });

        var trade = _sut.CreateSubmittedTrade(signal);

        trade.RequestedQuantity.Should().Be(7.5m);
        trade.RemainingQuantity.Should().Be(7.5m);
    }

    // -----------------------------------------------------------------------
    // 2. MarkAccepted
    // -----------------------------------------------------------------------

    [Fact]
    public void MarkAccepted_ShouldSetStatusToAccepted()
    {
        var trade = CreateActiveTrade();
        trade.LifecycleStatus = TradeLifecycleStatus.Submitted;

        _sut.MarkAccepted(trade);

        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Accepted);
        trade.LastUpdatedTime.Should().NotBeNull();
    }

    [Fact]
    public void MarkAccepted_ShouldUseProvidedTimestamp()
    {
        var trade = CreateActiveTrade();
        var timestamp = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        _sut.MarkAccepted(trade, timestamp);

        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Accepted);
        trade.LastUpdatedTime.Should().Be(timestamp);
    }

    // -----------------------------------------------------------------------
    // 3. MarkFailed
    // -----------------------------------------------------------------------

    [Fact]
    public void MarkFailed_ShouldSetStatusAndErrorMessage()
    {
        var trade = CreateActiveTrade();

        _sut.MarkFailed(trade, "Insufficient margin");

        trade.Status.Should().Be(TradeStatus.Failed);
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Failed);
        trade.ExitReason.Should().Be("Insufficient margin");
        trade.LastUpdatedTime.Should().NotBeNull();
    }

    // -----------------------------------------------------------------------
    // 4. ApplyExecutionFill
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyExecutionFill_ShouldUpdateTradeWithVenueData()
    {
        var signal = CreateSignal(price: 100m, quantity: 10m);
        var trackedTrade = _sut.CreateSubmittedTrade(signal);
        var venueEntryTime = new DateTime(2025, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        var venueTrade = new Trade
        {
            Symbol = "BTCUSDT",
            EntryPrice = 100.50m,
            Quantity = 10m,
            EntryTime = venueEntryTime,
            ExecutionLatencyMs = 42,
            EntrySlippagePercent = 0.005m,
            ExecutionMode = "REAL",
            PromotionStage = "LiveProbe"
        };

        _sut.ApplyExecutionFill(trackedTrade, venueTrade);

        trackedTrade.EntryPrice.Should().Be(100.50m);
        trackedTrade.MarkPrice.Should().Be(100.50m);
        trackedTrade.Quantity.Should().Be(10m);
        trackedTrade.FilledQuantity.Should().Be(10m);
        trackedTrade.RemainingQuantity.Should().Be(10m);
        trackedTrade.ClosedQuantity.Should().Be(0m);
        trackedTrade.Status.Should().Be(TradeStatus.Executed);
        trackedTrade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Active);
        trackedTrade.EntryTime.Should().Be(venueEntryTime);
        trackedTrade.ExecutionLatencyMs.Should().Be(42);
        trackedTrade.EntrySlippagePercent.Should().Be(0.005m);
        trackedTrade.OptimalPriceSinceEntry.Should().Be(100.50m);
        trackedTrade.ExitReason.Should().BeEmpty();
    }

    [Fact]
    public void ApplyExecutionFill_ShouldSetPartiallyFilled_WhenExecutedLessThanRequested()
    {
        var signal = CreateSignal(price: 100m, quantity: 10m);
        var trackedTrade = _sut.CreateSubmittedTrade(signal);
        var venueTrade = new Trade
        {
            EntryPrice = 100m,
            Quantity = 7m
        };

        _sut.ApplyExecutionFill(trackedTrade, venueTrade);

        trackedTrade.LifecycleStatus.Should().Be(TradeLifecycleStatus.PartiallyFilled);
        trackedTrade.Quantity.Should().Be(7m);
        trackedTrade.RequestedQuantity.Should().Be(10m);
    }

    // -----------------------------------------------------------------------
    // 5. ApplyBreakevenProtection — should move stop loss
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyBreakevenProtection_ShouldMoveStopLoss_WhenPriceExceedsEntry_ForBuy()
    {
        // Entry=100, TP=110 => target distance=10, trigger fraction=0.50 => trigger distance=5
        // Candle high must reach >= 105 to arm breakeven.
        // Buffer = 100 * 0.0005 = 0.05 => new SL = max(95, 100.05) = 100.05
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            stopLoss: 95m,
            takeProfit: 110m,
            breakevenTriggerFraction: 0.50m);

        var candle = CreateCandle(close: 106m, high: 106m, low: 104m);
        var previous = CreateCandle(close: 104m, high: 104m, low: 103m);

        _sut.AdvanceTrade(trade, candle, previous, atr: 1m);

        trade.IsBreakevenArmed.Should().BeTrue();
        trade.StopLoss.Should().Be(100.05m);
    }

    [Fact]
    public void ApplyBreakevenProtection_ShouldMoveStopLoss_WhenPriceExceedsEntry_ForSell()
    {
        // Entry=100, TP=90 => target distance=10, trigger fraction=0.50 => trigger distance=5
        // Candle low must reach <= 95 to arm breakeven.
        // Buffer = 100 * 0.0005 = 0.05 => new SL = min(105, 99.95) = 99.95
        var trade = CreateActiveTrade(
            side: TradeSide.Sell,
            entryPrice: 100m,
            stopLoss: 105m,
            takeProfit: 90m,
            breakevenTriggerFraction: 0.50m);

        var candle = CreateCandle(close: 94m, high: 96m, low: 94m);
        var previous = CreateCandle(close: 96m, high: 97m, low: 95m);

        _sut.AdvanceTrade(trade, candle, previous, atr: 1m);

        trade.IsBreakevenArmed.Should().BeTrue();
        trade.StopLoss.Should().Be(99.95m);
    }

    // -----------------------------------------------------------------------
    // 6. ApplyBreakevenProtection — should NOT move stop loss
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyBreakevenProtection_ShouldNotMoveStopLoss_WhenPriceBelowThreshold()
    {
        // Entry=100, TP=110, trigger fraction=0.50 => trigger at high >= 105
        // Candle high only reaches 103 => not triggered
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            stopLoss: 95m,
            takeProfit: 110m,
            breakevenTriggerFraction: 0.50m);

        var candle = CreateCandle(close: 103m, high: 103m, low: 101m);
        var previous = CreateCandle(close: 101m, high: 102m, low: 100m);

        _sut.AdvanceTrade(trade, candle, previous, atr: 1m);

        trade.IsBreakevenArmed.Should().BeFalse();
        trade.StopLoss.Should().Be(95m);
    }

    [Fact]
    public void ApplyBreakevenProtection_ShouldNotRearm_WhenAlreadyArmed()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            stopLoss: 100.05m,
            takeProfit: 110m,
            breakevenTriggerFraction: 0.50m);
        trade.IsBreakevenArmed = true;

        var originalStopLoss = trade.StopLoss;
        var candle = CreateCandle(close: 108m, high: 108m, low: 106m);
        var previous = CreateCandle(close: 106m, high: 107m, low: 105m);

        _sut.AdvanceTrade(trade, candle, previous, atr: 1m);

        trade.StopLoss.Should().Be(originalStopLoss);
    }

    // -----------------------------------------------------------------------
    // 7. ResolveExitReason — StopLoss
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveExitReason_ShouldReturnStopLoss_WhenPriceHitsStop()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            stopLoss: 95m,
            takeProfit: 110m);

        // Close <= StopLoss triggers StopLoss for Buy
        var candle = CreateCandle(close: 94m, high: 95m, low: 93m);
        var previous = CreateCandle(close: 96m, high: 97m, low: 95m);

        var action = _sut.AdvanceTrade(trade, candle, previous, atr: 0.1m);

        action.Should().NotBeNull();
        action!.ActionType.Should().Be(PositionLifecycleActionType.FullExit);
        action.Reason.Should().Be("StopLoss");
        action.Quantity.Should().Be(10m);
    }

    [Theory]
    [InlineData(TradeSide.Buy, 94, 95, "StopLoss")]
    [InlineData(TradeSide.Sell, 106, 105, "StopLoss")]
    public void ResolveExitReason_ShouldReturnStopLoss_ForBothSides(
        TradeSide side, double closePrice, double sl, string expectedReason)
    {
        var trade = CreateActiveTrade(
            side: side,
            entryPrice: 100m,
            stopLoss: (decimal)sl,
            takeProfit: 0m,
            breakevenTriggerFraction: 0m);

        var candle = CreateCandle(
            close: (decimal)closePrice,
            high: (decimal)closePrice + 1m,
            low: (decimal)closePrice - 1m);
        var previous = CreateCandle(close: 100m, high: 101m, low: 99m);

        var action = _sut.AdvanceTrade(trade, candle, previous, atr: 0.01m);

        action.Should().NotBeNull();
        action!.Reason.Should().Be(expectedReason);
    }

    // -----------------------------------------------------------------------
    // 8. ResolveExitReason — TakeProfit
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveExitReason_ShouldReturnTakeProfit_WhenPriceHitsTarget()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            stopLoss: 95m,
            takeProfit: 110m,
            breakevenTriggerFraction: 0m);

        // Close >= TakeProfit triggers TP for Buy
        var candle = CreateCandle(close: 111m, high: 112m, low: 109m);
        var previous = CreateCandle(close: 109m, high: 110m, low: 108m);

        var action = _sut.AdvanceTrade(trade, candle, previous, atr: 0.1m);

        action.Should().NotBeNull();
        action!.ActionType.Should().Be(PositionLifecycleActionType.FullExit);
        action.Reason.Should().Be("TakeProfit");
    }

    [Theory]
    [InlineData(TradeSide.Buy, 111, 110, "TakeProfit")]
    [InlineData(TradeSide.Sell, 89, 90, "TakeProfit")]
    public void ResolveExitReason_ShouldReturnTakeProfit_ForBothSides(
        TradeSide side, double closePrice, double tp, string expectedReason)
    {
        var trade = CreateActiveTrade(
            side: side,
            entryPrice: 100m,
            stopLoss: 0m,
            takeProfit: (decimal)tp,
            breakevenTriggerFraction: 0m);

        var candle = CreateCandle(
            close: (decimal)closePrice,
            high: (decimal)closePrice + 1m,
            low: (decimal)closePrice - 1m);
        var previous = CreateCandle(close: 100m, high: 101m, low: 99m);

        var action = _sut.AdvanceTrade(trade, candle, previous, atr: 0.01m);

        action.Should().NotBeNull();
        action!.Reason.Should().Be(expectedReason);
    }

    // -----------------------------------------------------------------------
    // 9. ResolveExitReason — TrailingStop
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveExitReason_ShouldReturnTrailingStop_WhenTrailingStopTriggered()
    {
        // Set up a Buy trade with no fixed SL/TP so only trailing can trigger.
        // TrailingStopService activates when OptimalPriceSinceEntry >= EntryPrice + ATR.
        // With ATR=2, activation threshold = 102.
        // Pre-set optimal to 108 so trailing is active.
        // Trail price = optimal - (atr * multiplier) = 108 - (2*2) = 104.
        // When candle low <= 104 and trailing > 0, trailing triggers.
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            stopLoss: 0m,
            takeProfit: 0m,
            breakevenTriggerFraction: 0m);
        trade.OptimalPriceSinceEntry = 108m;
        trade.TrailingStopPrice = 104m;

        var candle = CreateCandle(close: 103m, high: 104m, low: 103m);
        var previous = CreateCandle(close: 105m, high: 106m, low: 104m);

        var action = _sut.AdvanceTrade(trade, candle, previous, atr: 2m);

        action.Should().NotBeNull();
        action!.Reason.Should().Be("TrailingSL");
        action.ActionType.Should().Be(PositionLifecycleActionType.FullExit);
    }

    // -----------------------------------------------------------------------
    // 10. ApplyExitFill — PnL calculation
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyExitFill_ShouldCalculatePnlCorrectly_ForBuy()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            quantity: 10m);
        trade.ClosedQuantity = 0m;

        var exitTime = DateTime.UtcNow;
        _sut.ApplyExitFill(trade, exitPrice: 105m, exitQuantity: 10m, reason: "TakeProfit", exitTimeUtc: exitTime);

        trade.ExitPrice.Should().Be(105m);
        trade.AverageExitPrice.Should().Be(105m);
        trade.ClosedQuantity.Should().Be(10m);
        trade.Quantity.Should().Be(0m);
        trade.RemainingQuantity.Should().Be(0m);
        trade.ExitReason.Should().Be("TakeProfit");
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Closed);
        trade.ExitTime.Should().Be(exitTime);
        // RealisedPnL = (105 - 100) * 10 * 1 = 50
        trade.RealisedPnL.Should().Be(50m);
    }

    [Fact]
    public void ApplyExitFill_ShouldCalculatePnlCorrectly_ForSell()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Sell,
            entryPrice: 100m,
            quantity: 10m);
        trade.ClosedQuantity = 0m;

        var exitTime = DateTime.UtcNow;
        _sut.ApplyExitFill(trade, exitPrice: 95m, exitQuantity: 10m, reason: "TakeProfit", exitTimeUtc: exitTime);

        trade.ExitPrice.Should().Be(95m);
        trade.ClosedQuantity.Should().Be(10m);
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Closed);
        // RealisedPnL = (95 - 100) * 10 * (-1) = 50
        trade.RealisedPnL.Should().Be(50m);
    }

    [Fact]
    public void ApplyExitFill_ShouldSetPartialExit_WhenNotFullyClosed()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            quantity: 10m);
        trade.ClosedQuantity = 0m;

        var exitTime = DateTime.UtcNow;
        _sut.ApplyExitFill(trade, exitPrice: 105m, exitQuantity: 4m, reason: "PartialTakeProfit", exitTimeUtc: exitTime);

        trade.ClosedQuantity.Should().Be(4m);
        trade.Quantity.Should().Be(6m);
        trade.RemainingQuantity.Should().Be(6m);
        trade.HasTakenPartialProfit.Should().BeTrue();
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.PartialExit);
        trade.ExitTime.Should().BeNull();
    }

    [Fact]
    public void ApplyExitFill_ShouldDoNothing_WhenExitQuantityIsZero()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            quantity: 10m);
        var originalStatus = trade.LifecycleStatus;

        _sut.ApplyExitFill(trade, exitPrice: 105m, exitQuantity: 0m, reason: "Test", exitTimeUtc: DateTime.UtcNow);

        trade.LifecycleStatus.Should().Be(originalStatus);
        trade.Quantity.Should().Be(10m);
    }

    [Fact]
    public void ApplyExitFill_ShouldComputeWeightedAverageExitPrice_AcrossMultiplePartials()
    {
        var trade = CreateActiveTrade(
            side: TradeSide.Buy,
            entryPrice: 100m,
            quantity: 10m);
        trade.ClosedQuantity = 0m;
        var exitTime = DateTime.UtcNow;

        // First partial: exit 4 @ 105
        _sut.ApplyExitFill(trade, exitPrice: 105m, exitQuantity: 4m, reason: "PartialTakeProfit", exitTimeUtc: exitTime);

        // Second partial: exit remaining 6 @ 108
        _sut.ApplyExitFill(trade, exitPrice: 108m, exitQuantity: 6m, reason: "TakeProfit", exitTimeUtc: exitTime);

        // Weighted average = (105*4 + 108*6) / 10 = (420 + 648) / 10 = 106.8
        trade.AverageExitPrice.Should().Be(106.8m);
        trade.ClosedQuantity.Should().Be(10m);
        trade.Quantity.Should().Be(0m);
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Closed);
    }
}
