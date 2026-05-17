using System.Text.Json;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// One row per intended order in an EdgeLab dry-run cycle. Persisted as JSONL
/// so each cycle's set of intended orders becomes an append-only audit trail.
///
/// Dry-run never writes to <c>PaperPortfolio.ClosedPositions</c> — that would
/// pollute Funding-Long / Pairs-Long PnL attribution in the daily digest. This
/// ledger is the dedicated channel for dry-run trade telemetry.
/// </summary>
public sealed record EdgeLabDryRunEntry(
    DateTime TimestampUtc,
    DateTimeOffset AsOfBar,
    string Symbol,
    string Side,
    decimal Qty,
    decimal Price,
    decimal NotionalUsd,
    decimal TargetWeight,
    decimal DeltaUsd,
    decimal Basis);

/// <summary>
/// Append-only JSONL store for EdgeLab dry-run cycles. Singleton, lock-guarded.
/// File lives at <c>%LocalAppData%\ThatsMyQuant\edgelab_dryrun_log.jsonl</c>
/// (configurable for tests). At ~84 lines/day (6 cycles × 14 symbols) the file
/// is tiny — no rotation needed at this phase.
///
/// Read path is intentionally simple: stream the whole file, parse each line,
/// filter by timestamp. Cheap enough for daily digest scope; if it grows large
/// later, switch to date-suffixed files.
/// </summary>
public sealed class EdgeLabDryRunLedger
{
    private readonly object _lock = new();
    private readonly string _path;

    public EdgeLabDryRunLedger() : this(DefaultPath()) { }

    public EdgeLabDryRunLedger(string path)
    {
        _path = path;
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string FilePath => _path;

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThatsMyQuant",
        "edgelab_dryrun_log.jsonl");

    public void AppendCycle(IReadOnlyList<EdgeLabDryRunEntry> entries)
    {
        if (entries.Count == 0) return;
        lock (_lock)
        {
            using var sw = new StreamWriter(_path, append: true);
            foreach (var e in entries)
                sw.WriteLine(JsonSerializer.Serialize(e));
        }
    }

    /// <summary>Read entries timestamped after <paramref name="sinceUtc"/>.
    /// Lines that fail to parse are silently skipped — corruption from a
    /// crash mid-write should not break the digest.</summary>
    public IReadOnlyList<EdgeLabDryRunEntry> ReadSince(DateTime sinceUtc)
    {
        if (!File.Exists(_path)) return Array.Empty<EdgeLabDryRunEntry>();
        var result = new List<EdgeLabDryRunEntry>();
        lock (_lock)
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                EdgeLabDryRunEntry? entry;
                try { entry = JsonSerializer.Deserialize<EdgeLabDryRunEntry>(line); }
                catch (JsonException) { continue; }
                if (entry is null) continue;
                if (entry.TimestampUtc >= sinceUtc) result.Add(entry);
            }
        }
        return result;
    }
}
