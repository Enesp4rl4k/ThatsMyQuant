using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

public class EdgeLabSyntheticPortfolioTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public EdgeLabSyntheticPortfolioTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "edgelab-synth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "synth.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Empty_ledger_starts_with_no_positions_and_zero_pnl()
    {
        var p = new EdgeLabSyntheticPortfolio(_path);
        p.GetState().Positions.Should().BeEmpty();
        p.GetSignedQty("BTCUSDT").Should().Be(0m);
        var (total, per) = p.ComputePeriodPnL(new Dictionary<string, decimal> { ["BTCUSDT"] = 50000m });
        total.Should().Be(0m); per.Should().BeEmpty();
    }

    [Fact]
    public void Commit_then_PeriodPnL_computes_mark_to_mark()
    {
        var p = new EdgeLabSyntheticPortfolio(_path);
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        p.Commit(asOf, DateTime.UtcNow, new Dictionary<string, EdgeLabSyntheticPosition>
        {
            ["BTCUSDT"] = new(qty:0.05m,  mark:50_000m,  weight:0.5m),    // long $2500
            ["DOGEUSDT"] = new(qty:-347_222m,  mark:0.144m,  weight:-0.5m), // short $50k
        });

        // BTC up 4% to 52000 → +0.05 * 2000 = +$100 long
        // DOGE down 5% to 0.1368 → (-347222) * (-0.0072) = +$2500 short profit (approx)
        var (total, per) = p.ComputePeriodPnL(new Dictionary<string, decimal>
        {
            ["BTCUSDT"] = 52_000m,
            ["DOGEUSDT"] = 0.1368m,
        });
        per["BTCUSDT"].Should().BeApproximately(100m, 0.001m);
        per["DOGEUSDT"].Should().BeApproximately(2500m, 0.5m);
        total.Should().BeApproximately(2600m, 0.5m);
    }

    [Fact]
    public void PeriodPnL_skips_symbols_missing_current_mark()
    {
        var p = new EdgeLabSyntheticPortfolio(_path);
        p.Commit(DateTimeOffset.UtcNow, DateTime.UtcNow, new Dictionary<string, EdgeLabSyntheticPosition>
        {
            ["BTCUSDT"] = new(0.05m, 50_000m, 0.5m),
            ["XRPUSDT"] = new(1000m, 0.5m, 0.5m),
        });
        var (total, per) = p.ComputePeriodPnL(new Dictionary<string, decimal> { ["BTCUSDT"] = 51_000m });
        per.Keys.Should().Contain("BTCUSDT");
        per.Keys.Should().NotContain("XRPUSDT");
        total.Should().BeApproximately(50m, 0.001m);  // 0.05 * 1000
    }

    [Fact]
    public void State_persists_across_instances()
    {
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        {
            var p1 = new EdgeLabSyntheticPortfolio(_path);
            p1.Commit(asOf, DateTime.UtcNow, new Dictionary<string, EdgeLabSyntheticPosition>
            {
                ["ETHUSDT"] = new(1.5m, 3000m, 0.5m),
            });
        }
        var p2 = new EdgeLabSyntheticPortfolio(_path);
        p2.GetSignedQty("ETHUSDT").Should().Be(1.5m);
        p2.GetState().LastCycleAsOfBar.Should().Be(asOf);
    }

    [Fact]
    public void Corrupt_state_file_resets_to_empty_without_throwing()
    {
        File.WriteAllText(_path, "{not valid json");
        var p = new EdgeLabSyntheticPortfolio(_path);
        p.GetState().Positions.Should().BeEmpty();
    }

    [Fact]
    public void Reset_clears_state()
    {
        var p = new EdgeLabSyntheticPortfolio(_path);
        p.Commit(DateTimeOffset.UtcNow, DateTime.UtcNow, new Dictionary<string, EdgeLabSyntheticPosition>
        {
            ["BTCUSDT"] = new(1m, 50_000m, 0.5m),
        });
        p.Reset();
        p.GetState().Positions.Should().BeEmpty();
        p.GetState().LastCycleAsOfBar.Should().BeNull();
    }
}

public class EdgeLabReconciliationLedgerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public EdgeLabReconciliationLedgerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "edgelab-recon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "pnl.jsonl");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Append_and_ReadLast_round_trip()
    {
        var ledger = new EdgeLabReconciliationLedger(_path);
        var asOf = DateTimeOffset.Parse("2026-04-30T20:00:00+00:00");
        ledger.Append(new EdgeLabReconciliationEntry(
            TimestampUtc: DateTime.UtcNow,
            ThisCycleAsOfBar: asOf,
            PrevCycleAsOfBar: null,
            PeriodPnLUsd: 10.5m,
            CumulativePnLUsd: 10.5m,
            Basis: 5000m,
            PositionsHeld: 4,
            PerSymbolPnL: new() { ["BTCUSDT"] = 5m, ["DOGEUSDT"] = 5.5m }));

        var last = ledger.ReadLast()!;
        last.PeriodPnLUsd.Should().Be(10.5m);
        last.CumulativePnLUsd.Should().Be(10.5m);
        last.PerSymbolPnL["DOGEUSDT"].Should().Be(5.5m);
    }

    [Fact]
    public void ReadSince_filters_by_timestamp()
    {
        var ledger = new EdgeLabReconciliationLedger(_path);
        ledger.Append(new(DateTime.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-2),
            null, 1m, 1m, 5000m, 0, new()));
        ledger.Append(new(DateTime.UtcNow, DateTimeOffset.UtcNow, null, 2m, 3m, 5000m, 0, new()));

        var recent = ledger.ReadSince(DateTime.UtcNow.AddHours(-1));
        recent.Should().HaveCount(1);
        recent[0].PeriodPnLUsd.Should().Be(2m);
    }
}
