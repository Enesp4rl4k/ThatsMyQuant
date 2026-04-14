using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

/// <summary>
/// Unit tests for <see cref="MemoryPortfolioManager"/> (Application/Strategy — DI-registered version).
/// Validates: Requirements 1.6, 2.6
/// </summary>
public class MemoryPortfolioManagerTests
{
    // ── Test 1: SetBalance / GetAvailableBalance ──────────────────────────────

    [Fact]
    public void SetBalance_ThenGetAvailableBalance_ReturnsSetAmount()
    {
        var sut = new MemoryPortfolioManager();

        sut.SetBalance("USDT", 1000m);

        sut.GetAvailableBalance("USDT").Should().Be(1000m);
    }

    // ── Test 2: Lock / Unlock / floor at zero ─────────────────────────────────

    [Fact]
    public void Lock_ReducesAvailableBalance()
    {
        var sut = new MemoryPortfolioManager();
        sut.SetBalance("USDT", 1000m);

        sut.Lock("USDT", 300m);

        sut.GetAvailableBalance("USDT").Should().Be(700m);
    }

    [Fact]
    public void Unlock_RestoresAvailableBalance()
    {
        var sut = new MemoryPortfolioManager();
        sut.SetBalance("USDT", 1000m);
        sut.Lock("USDT", 300m);

        sut.Unlock("USDT", 300m);

        sut.GetAvailableBalance("USDT").Should().Be(1000m);
    }

    [Fact]
    public void GetAvailableBalance_NeverGoesBelowZero()
    {
        var sut = new MemoryPortfolioManager();
        sut.SetBalance("USDT", 100m);

        // Lock more than the total balance
        sut.Lock("USDT", 500m);

        sut.GetAvailableBalance("USDT").Should().Be(0m);
    }

    // ── Test 3: Long position P&L ─────────────────────────────────────────────

    [Fact]
    public void ClosePosition_Long_ReturnsPnlAndAddsToBalance()
    {
        var sut = new MemoryPortfolioManager();
        sut.SetBalance("USDT", 0m);

        sut.OpenPosition("BTCUSDT", 50_000m, 1m, isLong: true);
        var pnl = sut.ClosePosition("BTCUSDT", 51_000m);

        pnl.Should().Be(1000m);
        sut.GetTotalBalance("USDT").Should().Be(1000m);
    }

    // ── Test 4: Short position P&L ────────────────────────────────────────────

    [Fact]
    public void ClosePosition_Short_ReturnsPnlAndAddsToBalance()
    {
        var sut = new MemoryPortfolioManager();
        sut.SetBalance("USDT", 0m);

        sut.OpenPosition("BTCUSDT", 50_000m, 1m, isLong: false);
        var pnl = sut.ClosePosition("BTCUSDT", 49_000m);

        pnl.Should().Be(1000m);
        sut.GetTotalBalance("USDT").Should().Be(1000m);
    }

    // ── Test 5: UpdateFromExecutionReport — Executed fill ────────────────────

    [Fact]
    public void UpdateFromExecutionReport_Executed_OpensPositionWithFilledQuantity()
    {
        var sut = new MemoryPortfolioManager();

        var trade = new Trade
        {
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            EntryPrice = 50_000m,
            Quantity = 1m,
            FilledQuantity = 0.5m,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active
        };

        sut.UpdateFromExecutionReport(trade);

        var position = sut.GetPosition("BTCUSDT");
        position.Should().NotBeNull();
        position!.Quantity.Should().Be(0.5m);
    }

    [Fact]
    public void UpdateFromExecutionReport_ExecutedClosed_ClosesPositionAndBooksRealizedPnl()
    {
        var sut = new MemoryPortfolioManager();
        sut.SetBalance("USDT", 1000m);
        sut.OpenPosition("BTCUSDT", 50_000m, 0.1m, isLong: true);

        var trade = new Trade
        {
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            EntryPrice = 50_000m,
            ExitPrice = 51_500m,
            AverageExitPrice = 51_500m,
            Quantity = 0.1m,
            FilledQuantity = 0.1m,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Closed
        };

        sut.UpdateFromExecutionReport(trade);

        sut.GetPosition("BTCUSDT").Should().BeNull();
        sut.RealizedPnl.Should().Be(150m);
        sut.GetTotalBalance("USDT").Should().Be(1150m);
    }

    // ── Test 6: UpdateFromExecutionReport — Rejected unlocks notional ─────────

    [Fact]
    public void UpdateFromExecutionReport_Rejected_UnlocksEstimatedNotional()
    {
        var sut = new MemoryPortfolioManager();
        sut.SetBalance("USDT", 1000m);

        var entryPrice = 50_000m;
        var quantity = 0.01m;
        var estimatedNotional = entryPrice * quantity; // 500m

        sut.Lock("USDT", estimatedNotional);
        sut.GetAvailableBalance("USDT").Should().Be(500m); // locked 500 of 1000

        var trade = new Trade
        {
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            EntryPrice = entryPrice,
            Quantity = quantity,
            Status = TradeStatus.Rejected,
            LifecycleStatus = TradeLifecycleStatus.Rejected
        };

        sut.UpdateFromExecutionReport(trade);

        // After unlock, full 1000 should be available again
        sut.GetAvailableBalance("USDT").Should().Be(1000m);
    }

    [Fact]
    public void UpdateFromExecutionReport_FailedAndClosed_RemovesExistingPosition()
    {
        var sut = new MemoryPortfolioManager();
        sut.OpenPosition("ETHUSDT", 3000m, 1m, isLong: true);

        var trade = new Trade
        {
            Symbol = "ETHUSDT",
            EntryPrice = 3000m,
            Quantity = 1m,
            Status = TradeStatus.Failed,
            LifecycleStatus = TradeLifecycleStatus.Closed
        };

        sut.UpdateFromExecutionReport(trade);

        sut.GetPosition("ETHUSDT").Should().BeNull();
    }

    // ── Test 7: Reconcile ─────────────────────────────────────────────────────

    [Fact]
    public void Reconcile_SetsBalancesAndRemovesAbsentAssets()
    {
        var sut = new MemoryPortfolioManager();
        // Pre-populate an asset that won't be in the external source
        sut.SetBalance("BTC", 0.5m);
        sut.SetBalance("USDT", 500m);

        var external = new Dictionary<string, (decimal Total, decimal Locked)>
        {
            ["USDT"] = (2000m, 100m)
        };

        sut.Reconcile(external);

        sut.GetTotalBalance("USDT").Should().Be(2000m);
        sut.GetAvailableBalance("USDT").Should().Be(1900m); // 2000 - 100 locked
        sut.GetTotalBalance("BTC").Should().Be(0m);         // removed — absent from external
    }

    // ── Test 8: Thread-safety — 50 concurrent AddBalance calls ───────────────

    [Fact]
    public async Task AddBalance_ConcurrentCalls_ConvergeToCorrectTotal()
    {
        var sut = new MemoryPortfolioManager();
        sut.SetBalance("USDT", 0m);

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => sut.AddBalance("USDT", 10m)))
            .ToArray();

        await Task.WhenAll(tasks);

        sut.GetTotalBalance("USDT").Should().Be(500m);
    }

    // ── Test 9: GetDrawdownPercent ────────────────────────────────────────────

    [Fact]
    public void GetDrawdownPercent_BeforePeakSet_ReturnsZero()
    {
        var sut = new MemoryPortfolioManager();

        sut.GetDrawdownPercent().Should().Be(0m);
    }

    [Fact]
    public void GetDrawdownPercent_AfterBalanceDrop_ReturnsCorrectPercent()
    {
        var sut = new MemoryPortfolioManager();

        sut.SetBalance("USDT", 1000m); // peak = 1000
        sut.SetBalance("USDT", 900m);  // current = 900

        // drawdown = (1000 - 900) / 1000 * 100 = 10%
        sut.GetDrawdownPercent().Should().Be(10m);
    }

    [Fact]
    public void Reconcile_UpdatesTryLockedBalanceAndAvailableBalance()
    {
        var sut = new MemoryPortfolioManager();
        sut.SetBalance("TRY", 10_000m);

        var external = new Dictionary<string, (decimal Total, decimal Locked)>
        {
            ["TRY"] = (12_500m, 2_000m)
        };

        sut.Reconcile(external);

        sut.GetTotalBalance("TRY").Should().Be(12_500m);
        sut.GetAvailableBalance("TRY").Should().Be(10_500m);
    }

    [Fact]
    public void UpdateMarketPrices_TracksFavorableAndAdverseExcursions()
    {
        var sut = new MemoryPortfolioManager();
        sut.OpenPosition("BTCUSDT", 100m, 1m, isLong: true);

        sut.UpdateMarketPrices(new Dictionary<string, decimal>
        {
            ["BTCUSDT"] = 110m,
            ["ETHUSDT"] = 999m
        });

        sut.UpdateMarketPrices(new Dictionary<string, decimal>
        {
            ["BTCUSDT"] = 95m
        });

        var position = sut.GetPosition("BTCUSDT");
        position.Should().NotBeNull();
        position!.HighestPrice.Should().Be(110m);
        position.LowestPrice.Should().Be(95m);
        position.MaxFavorableExcursionPercent.Should().Be(10m);
        position.MaxAdverseExcursionPercent.Should().Be(-5m);
    }
}
