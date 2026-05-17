using System.Globalization;
using System.Text.Json;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// One row per fill × markout-horizon. Captures the venue mark price at
/// {+1s, +10s, +60s, +300s} after a fill so we can compute adverse-selection
/// bps over each horizon. Adverse selection = (mark − fillPrice) signed by side
/// at horizon T → if mark drops 5bp 60s after we BOUGHT, we ate 5bp of toxic
/// flow from a more-informed counterparty. High adverse selection on maker
/// fills is the canonical signal that we're picking off the book at the wrong
/// times — a precondition for tuning <c>MakerEdgeBps</c> or escalating to
/// VPIN-style toxic-flow detection.
///
/// Schema is intentionally flat JSON to make Pandas/Polars analysis trivial in
/// Quantlab. Operator can rsync the ledger to the laptop and run:
/// <code>
///   pl.read_ndjson(...).group_by(['strategy','horizon_s']).agg([pl.mean('adverse_bps')])
/// </code>
/// </summary>
public sealed record MarkoutEntry(
    DateTime FillUtc,
    DateTime SnapshotUtc,
    int HorizonSeconds,
    Guid TradeId,
    string Strategy,
    string Symbol,
    string Side,
    decimal FillPrice,
    decimal MarkPrice,
    decimal AdverseBps,
    string ExecutionSource);

/// <summary>
/// Append-only JSONL markout ledger. Lock-protected for the worker's multi-host-service
/// concurrent-fill case (low contention; markout snapshots fire on a 1-min timer).
/// </summary>
public sealed class MarkoutLedger
{
    private readonly object _lock = new();
    private readonly string _path;

    public MarkoutLedger() : this(DefaultPath()) { }

    public MarkoutLedger(string path)
    {
        _path = path;
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string FilePath => _path;

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThatsMyQuant",
        "markouts.jsonl");

    public void Append(MarkoutEntry entry)
    {
        lock (_lock)
        {
            using var sw = new StreamWriter(_path, append: true);
            sw.WriteLine(JsonSerializer.Serialize(entry));
        }
    }

    /// <summary>
    /// Pure-function adverse-selection calculator. Side semantics:
    ///   Buy:  positive adverse means mark dropped after we bought (we paid above future mark — toxic).
    ///   Sell: positive adverse means mark rose after we sold (we sold below future mark — toxic).
    /// Returns 0 when fillPrice is non-positive (defensive — bad input shouldn't poison the ledger).
    /// </summary>
    public static decimal ComputeAdverseBps(string side, decimal fillPrice, decimal markPrice)
    {
        if (fillPrice <= 0m) return 0m;
        var sideSign = side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        // BUY:  adverse = (fill - mark) / fill — positive if mark fell below fill (we overpaid)
        // SELL: adverse = (mark - fill) / fill — positive if mark rose above fill (we undersold)
        var raw = sideSign * (fillPrice - markPrice) / fillPrice;
        return raw * 10_000m;
    }
}
