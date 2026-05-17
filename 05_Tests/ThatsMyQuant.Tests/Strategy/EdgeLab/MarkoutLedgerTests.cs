using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

/// <summary>
/// Unit tests for <see cref="MarkoutLedger.ComputeAdverseBps"/> + ledger persistence.
/// Sprint 4 (2026-05-10).
///
/// Adverse selection sign convention:
///   BUY  fill at $100, mark drops to $99.95 → we OVERPAID by 5 bp → adverse=+5 bp.
///   BUY  fill at $100, mark rises to $100.05 → we got a good fill → adverse=-5 bp.
///   SELL fill at $100, mark rises to $100.05 → we UNDERSOLD by 5 bp → adverse=+5 bp.
///   SELL fill at $100, mark drops to $99.95 → we got a good fill → adverse=-5 bp.
/// </summary>
public class MarkoutLedgerTests
{
    [Fact]
    public void ComputeAdverseBps_Buy_MarkDropped_PositiveAdverse()
    {
        // Bought at 100, mark fell to 99.95 → 5 bp adverse selection.
        var bps = MarkoutLedger.ComputeAdverseBps("Buy", 100m, 99.95m);
        bps.Should().BeApproximately(5m, 1e-6m);
    }

    [Fact]
    public void ComputeAdverseBps_Buy_MarkRose_NegativeAdverse()
    {
        // Bought at 100, mark went to 100.05 → favorable fill (negative adverse).
        var bps = MarkoutLedger.ComputeAdverseBps("Buy", 100m, 100.05m);
        bps.Should().BeApproximately(-5m, 1e-6m);
    }

    [Fact]
    public void ComputeAdverseBps_Sell_MarkRose_PositiveAdverse()
    {
        // Sold at 100, mark went to 100.05 → undersold by 5 bp.
        var bps = MarkoutLedger.ComputeAdverseBps("Sell", 100m, 100.05m);
        bps.Should().BeApproximately(5m, 1e-6m);
    }

    [Fact]
    public void ComputeAdverseBps_Sell_MarkDropped_NegativeAdverse()
    {
        // Sold at 100, mark fell to 99.95 → favorable fill.
        var bps = MarkoutLedger.ComputeAdverseBps("Sell", 100m, 99.95m);
        bps.Should().BeApproximately(-5m, 1e-6m);
    }

    [Fact]
    public void ComputeAdverseBps_NoMove_Zero()
    {
        MarkoutLedger.ComputeAdverseBps("Buy", 100m, 100m).Should().Be(0m);
        MarkoutLedger.ComputeAdverseBps("Sell", 100m, 100m).Should().Be(0m);
    }

    [Fact]
    public void ComputeAdverseBps_InvalidFill_ReturnsZero()
    {
        // Defensive: zero/negative fill price should not poison the ledger.
        MarkoutLedger.ComputeAdverseBps("Buy", 0m, 100m).Should().Be(0m);
        MarkoutLedger.ComputeAdverseBps("Buy", -1m, 100m).Should().Be(0m);
    }

    [Fact]
    public void ComputeAdverseBps_SideCaseInsensitive()
    {
        MarkoutLedger.ComputeAdverseBps("BUY", 100m, 99.95m)
            .Should().BeApproximately(5m, 1e-6m);
        MarkoutLedger.ComputeAdverseBps("buy", 100m, 99.95m)
            .Should().BeApproximately(5m, 1e-6m);
    }

    [Fact]
    public void Ledger_Append_PersistsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"markout_test_{Guid.NewGuid():N}.jsonl");
        try
        {
            var ledger = new MarkoutLedger(path);
            var entry = new MarkoutEntry(
                FillUtc: new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                SnapshotUtc: new DateTime(2026, 5, 10, 12, 5, 0, DateTimeKind.Utc),
                HorizonSeconds: 300,
                TradeId: Guid.NewGuid(),
                Strategy: "fee_panic_btc_long",
                Symbol: "BTCUSDT",
                Side: "Buy",
                FillPrice: 78_000m,
                MarkPrice: 78_039m,
                AdverseBps: -5m,
                ExecutionSource: "PAPER");
            ledger.Append(entry);

            File.Exists(path).Should().BeTrue();
            var lines = File.ReadAllLines(path);
            lines.Should().HaveCount(1);
            lines[0].Should().Contain("fee_panic_btc_long");
            lines[0].Should().Contain("\"HorizonSeconds\":300");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
