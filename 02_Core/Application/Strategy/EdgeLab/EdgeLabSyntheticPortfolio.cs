using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// EdgeLab-only synthetic position book. Solves two problems:
///
/// <list type="bullet">
///   <item><b>Turnover realism in dry-run.</b> Without this we'd compute deltas
///   against a zero baseline every cycle, simulating full re-entry and
///   overstating churn / fee exposure. With it, cycle-2+ deltas reflect only
///   what changed in the rank — same as live will behave.</item>
///   <item><b>Per-strategy attribution.</b> Live mode would otherwise read OKX
///   net positions contaminated by Funding-Long / Pairs-Long. EdgeLab maintains
///   ITS OWN book here; live execution drives this book and OKX in parallel,
///   and PnL math is always against this clean book.</item>
/// </list>
///
/// Persisted to disk so worker restart inherits state. JSON (single file, full
/// rewrite per cycle) — at 14 symbols × 2 fields it's ~1KB; no need for JSONL
/// append semantics.
/// </summary>
public sealed class EdgeLabSyntheticPortfolio
{
    private readonly object _lock = new();
    private readonly string _path;
    private EdgeLabSyntheticState _state = new();

    public EdgeLabSyntheticPortfolio() : this(DefaultPath()) { }

    public EdgeLabSyntheticPortfolio(string path)
    {
        _path = path;
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Load();
    }

    public string FilePath => _path;

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThatsMyQuant",
        "edgelab_synthetic_portfolio.json");

    /// <summary>Snapshot of current state. Returned as a copy so callers can iterate
    /// without holding the lock.</summary>
    public EdgeLabSyntheticState GetState()
    {
        lock (_lock)
        {
            return new EdgeLabSyntheticState
            {
                LastCycleAsOfBar = _state.LastCycleAsOfBar,
                LastCycleAppliedAtUtc = _state.LastCycleAppliedAtUtc,
                Positions = new Dictionary<string, EdgeLabSyntheticPosition>(_state.Positions, StringComparer.Ordinal),
            };
        }
    }

    /// <summary>Look up signed quantity for a symbol; 0 if untracked.</summary>
    public decimal GetSignedQty(string symbol)
    {
        lock (_lock)
        {
            return _state.Positions.TryGetValue(symbol, out var p) ? p.SignedQty : 0m;
        }
    }

    /// <summary>Mark-to-mark PnL between the previously stored marks and the supplied
    /// <paramref name="currentMarks"/>. Returns per-symbol breakdown plus the total.
    /// Symbols held with zero qty contribute zero. Symbols missing a current mark are
    /// skipped (returns the prior mark unchanged would be misleading; better to omit).
    /// This is the ex-ante realised return × position-from-last-cycle that
    /// signals/README.md §Reconciliation talks about.</summary>
    public (decimal Total, Dictionary<string, decimal> PerSymbol) ComputePeriodPnL(
        IReadOnlyDictionary<string, decimal> currentMarks)
    {
        lock (_lock)
        {
            var per = new Dictionary<string, decimal>(StringComparer.Ordinal);
            decimal total = 0m;
            foreach (var (sym, pos) in _state.Positions)
            {
                if (pos.SignedQty == 0m) continue;
                if (!currentMarks.TryGetValue(sym, out var newMark)) continue;
                if (pos.MarkPrice <= 0m || newMark <= 0m) continue;
                // Period PnL = signed qty × (markNew - markOld). signed_qty already encodes side.
                var pnl = pos.SignedQty * (newMark - pos.MarkPrice);
                per[sym] = pnl;
                total += pnl;
            }
            return (total, per);
        }
    }

    /// <summary>Apply a fully-rebalanced cycle: replace book with the supplied target
    /// positions. Caller has already done delta math + (in dry-run) skipped OKX or
    /// (live) submitted orders; this records the post-cycle state. Atomically
    /// rewrites the JSON file under the lock — partial state never persists.</summary>
    public void Commit(DateTimeOffset asOfBar, DateTime appliedAtUtc, IReadOnlyDictionary<string, EdgeLabSyntheticPosition> postCyclePositions)
    {
        lock (_lock)
        {
            _state = new EdgeLabSyntheticState
            {
                LastCycleAsOfBar = asOfBar,
                LastCycleAppliedAtUtc = appliedAtUtc,
                Positions = new Dictionary<string, EdgeLabSyntheticPosition>(postCyclePositions, StringComparer.Ordinal),
            };
            Save();
        }
    }

    /// <summary>Wipe state (test fixture support and explicit operator reset).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = new EdgeLabSyntheticState();
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<EdgeLabSyntheticState>(json);
            if (loaded is not null) _state = loaded;
        }
        catch (JsonException)
        {
            // Corrupt file → start clean rather than crash. Operator will see the
            // empty state in the digest and can investigate the orphaned file.
            _state = new EdgeLabSyntheticState();
        }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}

public sealed class EdgeLabSyntheticState
{
    [JsonPropertyName("last_cycle_as_of_bar")]
    public DateTimeOffset? LastCycleAsOfBar { get; set; }

    [JsonPropertyName("last_cycle_applied_at_utc")]
    public DateTime? LastCycleAppliedAtUtc { get; set; }

    [JsonPropertyName("positions")]
    public Dictionary<string, EdgeLabSyntheticPosition> Positions { get; set; }
        = new(StringComparer.Ordinal);
}

/// <summary>Synthetic position. <see cref="SignedQty"/> > 0 = long, &lt; 0 = short.
/// <see cref="MarkPrice"/> is the price at which this cycle's positions were sized
/// (used to compute period PnL on the next cycle).</summary>
public sealed class EdgeLabSyntheticPosition
{
    [JsonPropertyName("signed_qty")] public decimal SignedQty { get; set; }
    [JsonPropertyName("mark_price")] public decimal MarkPrice { get; set; }
    [JsonPropertyName("target_weight")] public decimal TargetWeight { get; set; }

    public EdgeLabSyntheticPosition() { }
    public EdgeLabSyntheticPosition(decimal qty, decimal mark, decimal weight)
    { SignedQty = qty; MarkPrice = mark; TargetWeight = weight; }
}
