using System.Text.Json;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// One row per cycle: hypothetical mark-to-mark PnL implied by the synthetic
/// positions held going INTO this cycle, evaluated against this cycle's marks.
///
/// This is the data point signals/README.md §Reconciliation says to track —
/// "expected returns from target_weights × realized_returns vs actual fill PnL".
/// In dry-run "actual fill PnL" doesn't exist, but the hypothetical version
/// here lets us answer the harder question: <i>does the bot's cycle behavior
/// reproduce EdgeLab's backtest performance ex-ante?</i>
///
/// At promotion-gate time (post-30 cycles), we compare cumulative
/// hypothetical PnL distribution vs the EdgeLab backtest distribution and
/// only flip live if the agreement is within band.
/// </summary>
public sealed record EdgeLabReconciliationEntry(
    DateTime TimestampUtc,
    DateTimeOffset ThisCycleAsOfBar,
    DateTimeOffset? PrevCycleAsOfBar,
    decimal PeriodPnLUsd,
    decimal CumulativePnLUsd,
    decimal Basis,
    int PositionsHeld,
    Dictionary<string, decimal> PerSymbolPnL);

/// <summary>
/// Append-only JSONL ledger for cycle-level hypothetical PnL. Read by daily
/// digest + (future) promotion gate evaluator.
/// </summary>
public sealed class EdgeLabReconciliationLedger
{
    private readonly object _lock = new();
    private readonly string _path;

    public EdgeLabReconciliationLedger() : this(DefaultPath()) { }

    public EdgeLabReconciliationLedger(string path)
    {
        _path = path;
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string FilePath => _path;

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThatsMyQuant",
        "edgelab_dryrun_pnl.jsonl");

    public void Append(EdgeLabReconciliationEntry entry)
    {
        lock (_lock)
        {
            using var sw = new StreamWriter(_path, append: true);
            sw.WriteLine(JsonSerializer.Serialize(entry));
        }
    }

    public IReadOnlyList<EdgeLabReconciliationEntry> ReadSince(DateTime sinceUtc)
    {
        if (!File.Exists(_path)) return Array.Empty<EdgeLabReconciliationEntry>();
        var result = new List<EdgeLabReconciliationEntry>();
        lock (_lock)
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                EdgeLabReconciliationEntry? entry;
                try { entry = JsonSerializer.Deserialize<EdgeLabReconciliationEntry>(line); }
                catch (JsonException) { continue; }
                if (entry is null) continue;
                if (entry.TimestampUtc >= sinceUtc) result.Add(entry);
            }
        }
        return result;
    }

    /// <summary>
    /// Counts non-empty rows in the ledger. Used by the Sprint 2 promotion gate
    /// (<see cref="EdgeLabPromotionGate.Resolve"/>) as the "paper cycles
    /// completed" signal — one row is appended per dry-run cycle. Cheap line
    /// count: O(file size), no JSON parse. Returns 0 when the file is missing.
    /// </summary>
    public int CountEntries()
    {
        if (!File.Exists(_path)) return 0;
        var n = 0;
        lock (_lock)
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (!string.IsNullOrWhiteSpace(line)) n++;
            }
        }
        return n;
    }

    /// <summary>Latest entry, regardless of age. For "show me the running total"
    /// readouts in the digest. Returns null if ledger is empty.</summary>
    public EdgeLabReconciliationEntry? ReadLast()
    {
        if (!File.Exists(_path)) return null;
        lock (_lock)
        {
            EdgeLabReconciliationEntry? last = null;
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<EdgeLabReconciliationEntry>(line);
                    if (e is not null) last = e;
                }
                catch (JsonException) { /* skip */ }
            }
            return last;
        }
    }
}
