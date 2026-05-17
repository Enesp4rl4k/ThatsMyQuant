using FluentAssertions;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy.EdgeLab;

public class EdgeLabDryRunLedgerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public EdgeLabDryRunLedgerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "edgelab-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "log.jsonl");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static EdgeLabDryRunEntry MakeEntry(DateTime ts, string sym, string side, decimal weight) =>
        new(ts, ts, sym, side, Qty: 1m, Price: 100m, NotionalUsd: 50_000m,
            TargetWeight: weight, DeltaUsd: 50_000m, Basis: 100_000m);

    [Fact]
    public void Append_then_ReadSince_round_trips()
    {
        var ledger = new EdgeLabDryRunLedger(_path);
        var t0 = new DateTime(2026, 4, 30, 20, 0, 0, DateTimeKind.Utc);
        ledger.AppendCycle(new[]
        {
            MakeEntry(t0, "BTCUSDT", "Buy", 0.5m),
            MakeEntry(t0, "DOGEUSDT", "Sell", -0.5m),
        });

        var read = ledger.ReadSince(t0.AddMinutes(-1));
        read.Should().HaveCount(2);
        read.Select(e => e.Symbol).Should().Contain(new[] { "BTCUSDT", "DOGEUSDT" });
        read.Single(e => e.Symbol == "DOGEUSDT").Side.Should().Be("Sell");
    }

    [Fact]
    public void ReadSince_filters_by_timestamp()
    {
        var ledger = new EdgeLabDryRunLedger(_path);
        var old = new DateTime(2026, 4, 28, 20, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2026, 4, 30, 20, 0, 0, DateTimeKind.Utc);
        ledger.AppendCycle(new[] { MakeEntry(old, "ETHUSDT", "Buy", 0.5m) });
        ledger.AppendCycle(new[] { MakeEntry(recent, "BTCUSDT", "Buy", 0.5m) });

        var read = ledger.ReadSince(new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc));
        read.Should().HaveCount(1);
        read[0].Symbol.Should().Be("BTCUSDT");
    }

    [Fact]
    public void Append_is_appendonly_across_calls()
    {
        var ledger = new EdgeLabDryRunLedger(_path);
        ledger.AppendCycle(new[] { MakeEntry(DateTime.UtcNow.AddHours(-1), "BTCUSDT", "Buy", 0.5m) });
        ledger.AppendCycle(new[] { MakeEntry(DateTime.UtcNow, "ETHUSDT", "Sell", -0.5m) });

        File.ReadAllLines(_path).Should().HaveCount(2);
        ledger.ReadSince(DateTime.UtcNow.AddHours(-2)).Should().HaveCount(2);
    }

    [Fact]
    public void ReadSince_skips_corrupt_lines()
    {
        var ledger = new EdgeLabDryRunLedger(_path);
        ledger.AppendCycle(new[] { MakeEntry(DateTime.UtcNow, "BTCUSDT", "Buy", 0.5m) });
        File.AppendAllText(_path, "this is not json\n{also not\n");

        // Should not throw, should return only the valid entry.
        var read = ledger.ReadSince(DateTime.UtcNow.AddHours(-1));
        read.Should().HaveCount(1);
        read[0].Symbol.Should().Be("BTCUSDT");
    }

    [Fact]
    public void ReadSince_returns_empty_when_file_missing()
    {
        var ledger = new EdgeLabDryRunLedger(Path.Combine(_tempDir, "nonexistent.jsonl"));
        ledger.ReadSince(DateTime.UtcNow.AddHours(-1)).Should().BeEmpty();
    }

    [Fact]
    public void AppendCycle_with_empty_list_is_noop()
    {
        var ledger = new EdgeLabDryRunLedger(_path);
        ledger.AppendCycle(Array.Empty<EdgeLabDryRunEntry>());
        File.Exists(_path).Should().BeFalse(); // never opened the file
    }
}
