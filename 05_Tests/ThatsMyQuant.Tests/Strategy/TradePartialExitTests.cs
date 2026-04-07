using FluentAssertions;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class TradePartialExitTests
{
    [Fact]
    public void PnlPercent_FullyClosed_BuyTrade_ShouldCalculateCorrectly()
    {
        var trade = new Trade
        {
            EntryPrice = 100m,
            ExitPrice = 110m,
            Quantity = 10m,
            Side = TradeSide.Buy,
            LifecycleStatus = TradeLifecycleStatus.Closed,
            ExitTime = DateTime.UtcNow
        };

        trade.PnlPercent.Should().Be(0.10m, "10% gain on buy from 100 to 110");
    }

    [Fact]
    public void PnlPercent_FullyClosed_SellTrade_ShouldCalculateCorrectly()
    {
        var trade = new Trade
        {
            EntryPrice = 100m,
            ExitPrice = 90m,
            Quantity = 10m,
            Side = TradeSide.Sell,
            LifecycleStatus = TradeLifecycleStatus.Closed,
            ExitTime = DateTime.UtcNow
        };

        trade.PnlPercent.Should().Be(0.10m, "10% gain on short from 100 to 90");
    }

    [Fact]
    public void PnlPercent_PartialExit_ShouldWeightClosedAndOpenPortions()
    {
        var trade = new Trade
        {
            EntryPrice = 100m,
            Quantity = 10m,
            ClosedQuantity = 5m,
            AverageExitPrice = 120m,  // 20% gain on closed portion
            MarkPrice = 110m,          // 10% unrealized gain on open portion
            Side = TradeSide.Buy,
            LifecycleStatus = TradeLifecycleStatus.PartialExit
        };

        // Weighted: (20% * 5/10) + (10% * 5/10) = 10% + 5% = 15%
        trade.PnlPercent.Should().Be(0.15m, "weighted average of closed 20% and open 10% gains");
    }

    [Fact]
    public void PnlPercent_PartialExit_ShortSide_ShouldCalculateCorrectly()
    {
        var trade = new Trade
        {
            EntryPrice = 100m,
            Quantity = 10m,
            ClosedQuantity = 4m,
            AverageExitPrice = 90m,   // 10% gain on short close
            MarkPrice = 95m,           // 5% unrealized gain on open short
            Side = TradeSide.Sell,
            LifecycleStatus = TradeLifecycleStatus.PartialExit
        };

        // Weighted: (10% * 4/10) + (5% * 6/10) = 4% + 3% = 7%
        trade.PnlPercent.Should().Be(0.07m, "weighted short P&L across closed and open portions");
    }

    [Fact]
    public void PnlPercent_NoExit_OpenTrade_WithMarkPrice_ShouldShowUnrealized()
    {
        var trade = new Trade
        {
            EntryPrice = 100m,
            Quantity = 10m,
            MarkPrice = 105m,
            Side = TradeSide.Buy,
            LifecycleStatus = TradeLifecycleStatus.Active
        };

        // No closed quantity, but MarkPrice is set → should show unrealized P&L
        trade.PnlPercent.Should().Be(0.05m, "5% unrealized gain with mark price");
    }

    [Fact]
    public void PnlPercent_NoExit_NoMarkPrice_ShouldReturnZero()
    {
        var trade = new Trade
        {
            EntryPrice = 100m,
            Quantity = 10m,
            Side = TradeSide.Buy,
            LifecycleStatus = TradeLifecycleStatus.Active
        };

        trade.PnlPercent.Should().Be(0m, "no exit price and no mark price means no P&L");
    }

    [Fact]
    public void PnlPercent_ZeroEntryPrice_ShouldReturnZero()
    {
        var trade = new Trade
        {
            EntryPrice = 0m,
            ExitPrice = 110m,
            Quantity = 10m,
            Side = TradeSide.Buy,
            LifecycleStatus = TradeLifecycleStatus.Closed,
            ExitTime = DateTime.UtcNow
        };

        trade.PnlPercent.Should().Be(0m, "zero entry price means no P&L calculation");
    }

    [Fact]
    public void RealisedPnL_PartialClose_ShouldUseClosedQuantity()
    {
        var trade = new Trade
        {
            EntryPrice = 100m,
            Quantity = 10m,
            ClosedQuantity = 6m,
            AverageExitPrice = 115m,
            Side = TradeSide.Buy,
            LifecycleStatus = TradeLifecycleStatus.PartialExit
        };

        // (115 - 100) * 6 * 1 = 90
        trade.RealisedPnL.Should().Be(90m);
    }

    [Fact]
    public void RealisedPnL_FullyClosed_NoClosedQuantity_ShouldUseQuantity()
    {
        var trade = new Trade
        {
            EntryPrice = 100m,
            ExitPrice = 120m,
            Quantity = 5m,
            Side = TradeSide.Buy,
            LifecycleStatus = TradeLifecycleStatus.Closed,
            ExitTime = DateTime.UtcNow
        };

        // (120 - 100) * 5 * 1 = 100
        trade.RealisedPnL.Should().Be(100m);
    }

    [Fact]
    public void PnlPercent_UsesAverageExitPrice_OverExitPrice()
    {
        var trade = new Trade
        {
            EntryPrice = 100m,
            ExitPrice = 105m,
            AverageExitPrice = 110m,
            Quantity = 10m,
            Side = TradeSide.Buy,
            LifecycleStatus = TradeLifecycleStatus.Closed,
            ExitTime = DateTime.UtcNow
        };

        // AverageExitPrice takes precedence
        trade.PnlPercent.Should().Be(0.10m, "AverageExitPrice (110) should be used over ExitPrice (105)");
    }
}
