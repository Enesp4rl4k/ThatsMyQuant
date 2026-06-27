using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

/// <summary>
/// Unit tests for the pure mapping in <see cref="ClosedPositionTradeMapper"/>
/// (the 2026-06-12 ledger-drift bridge: PaperPortfolio.ClosedPositions → db.Trades, the
/// feed the kill switch + per-strategy digest read). Guards two invariants:
///   1. Idempotency — the same close always hashes to the same Trade id.
///   2. PnL fidelity — a mirrored Trade reproduces the position's realized net under the
///      exact price-based formula the consumers use (sideMul·(exit−entry)·qty − fees + funding).
/// </summary>
public class ClosedPositionToTradeMirrorTests
{
    private static PaperPosition EdgeLabShortClose() => new()
    {
        StrategyTag = "op_breakout_4h_short",
        Symbol = "OPUSDT",
        Side = TradeSide.Sell,
        EntryPrice = 2.000m,
        CurrentPrice = 1.900m,     // exit below entry → short profits
        Quantity = 100m,
        EntryTime = new DateTime(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc),
        ExitTime = new DateTime(2026, 6, 12, 16, 0, 0, DateTimeKind.Utc),
        RealFeePaid = 1.20m,
        NetFunding = 0.30m,
        ExecutionMode = ClosedPositionTradeMapper.LiveExecutionMarker,
        PromotionStage = "Live",
        RealizedPnL = 9.10m,       // (2.0-1.9)*100 - 1.20 + 0.30
    };

    /// <summary>The consumer formula (EdgeLabKillSwitch + PerStrategyDigest both use this).</summary>
    private static decimal ConsumerNet(Trade t)
    {
        var sideMul = t.Side == TradeSide.Buy ? 1m : -1m;
        var gross = sideMul * (t.ExitPrice - t.EntryPrice) * t.Quantity;
        return gross - t.TotalFeesPaid + t.NetFunding;
    }

    [Fact]
    public void MirrorId_IsDeterministic_ForSameClose()
    {
        // Two freshly-built objects for the same close (as the 5-min LIVE_OKX rebuild produces)
        // must hash identically so we never double-insert.
        var a = EdgeLabShortClose();
        var b = EdgeLabShortClose();

        ClosedPositionTradeMapper.MirrorId(a)
            .Should().Be(ClosedPositionTradeMapper.MirrorId(b));
    }

    [Fact]
    public void MirrorId_Differs_WhenQuantityOrTimeChanges()
    {
        var baseId = ClosedPositionTradeMapper.MirrorId(EdgeLabShortClose());

        var diffQty = EdgeLabShortClose(); diffQty.Quantity = 101m;
        var diffExit = EdgeLabShortClose(); diffExit.ExitTime = diffExit.ExitTime!.Value.AddSeconds(1);

        ClosedPositionTradeMapper.MirrorId(diffQty).Should().NotBe(baseId);
        ClosedPositionTradeMapper.MirrorId(diffExit).Should().NotBe(baseId);
    }

    [Fact]
    public void MapToTrade_ReproducesRealizedNet_ForShort()
    {
        var pos = EdgeLabShortClose();
        var id = ClosedPositionTradeMapper.MirrorId(pos);

        var trade = ClosedPositionTradeMapper.MapToTrade(id, pos);

        // The consumers' recomputed net must match the position's realized net.
        ConsumerNet(trade).Should().BeApproximately(pos.RealizedPnL, 0.0001m);
        trade.LifecycleStatus.Should().Be(TradeLifecycleStatus.Closed);
        trade.StrategyTag.Should().Be("op_breakout_4h_short");
        trade.ExitPrice.Should().Be(1.900m);
        trade.ExecutionSource.Should().Be("LIVE");   // OKX-mirror marker → LIVE
    }

    [Fact]
    public void MapToTrade_ReproducesRealizedNet_ForLong()
    {
        var pos = EdgeLabShortClose();
        pos.StrategyTag = "bnb_breakout_4h_long";
        pos.Side = TradeSide.Buy;
        pos.EntryPrice = 600m;
        pos.CurrentPrice = 615m;       // exit above entry → long profits
        pos.Quantity = 0.5m;
        pos.RealFeePaid = 0.40m;
        pos.NetFunding = -0.10m;
        pos.RealizedPnL = (615m - 600m) * 0.5m - 0.40m + (-0.10m); // 6.50

        var trade = ClosedPositionTradeMapper.MapToTrade(
            ClosedPositionTradeMapper.MirrorId(pos), pos);

        ConsumerNet(trade).Should().BeApproximately(pos.RealizedPnL, 0.0001m);
        trade.Side.Should().Be(TradeSide.Buy);
    }

    [Fact]
    public void MapToTrade_PaperOnlyClose_TaggedPaperMirror()
    {
        var pos = EdgeLabShortClose();
        pos.ExecutionMode = "PAPER";   // not the OKX mirror marker

        var trade = ClosedPositionTradeMapper.MapToTrade(
            ClosedPositionTradeMapper.MirrorId(pos), pos);

        trade.ExecutionSource.Should().Be(ClosedPositionTradeMapper.PaperMirrorSource);
    }

    [Fact]
    public void MapToTrade_NullSignalId_FallsBackToMirrorId() // was Guid.Empty → UNIQUE collision
    {
        var pos = EdgeLabShortClose();
        pos.SignalId = null;           // paper-only close: no originating signal
        var id = ClosedPositionTradeMapper.MirrorId(pos);

        var trade = ClosedPositionTradeMapper.MapToTrade(id, pos);

        trade.SignalId.Should().Be(id);
        trade.SignalId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void MapToTrade_TwoDistinctNullSignalCloses_GetDistinctSignalIds()
    {
        // The production bug: two paper closes both mapped SignalId→Guid.Empty, so the
        // second insert tripped UNIQUE(Trades.SignalId). Distinct closes must now differ.
        var a = EdgeLabShortClose(); a.SignalId = null;
        var b = EdgeLabShortClose(); b.SignalId = null; b.Symbol = "ARBUSDT"; b.Quantity = 50m;

        var ta = ClosedPositionTradeMapper.MapToTrade(ClosedPositionTradeMapper.MirrorId(a), a);
        var tb = ClosedPositionTradeMapper.MapToTrade(ClosedPositionTradeMapper.MirrorId(b), b);

        ta.SignalId.Should().NotBe(tb.SignalId);
    }

    [Fact]
    public void MapToTrade_RealSignalId_IsPreserved()
    {
        var pos = EdgeLabShortClose();
        var realSignal = Guid.NewGuid();
        pos.SignalId = realSignal;

        var trade = ClosedPositionTradeMapper.MapToTrade(ClosedPositionTradeMapper.MirrorId(pos), pos);

        trade.SignalId.Should().Be(realSignal);
    }
}
