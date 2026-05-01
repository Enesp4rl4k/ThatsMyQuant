using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Timestamp-aware reader for the spread_snapshots.csv that
/// <see cref="OkxSymbolCostProfileProvider"/> writes on every cache miss. Lets backtests
/// look up "what was the bid/ask spread on this symbol around this bar's timestamp"
/// using nearest-neighbor matching, instead of the legacy "today's spread × every
/// historical bar" hack that systematically biased backtests.
///
/// Lookup semantics (in order of preference):
///   1. EXACT-DAY match: any snapshot for the same instId where |Δt| ≤ 24h. Pick
///      the closest. Restores per-day calibration when the dataset has good coverage.
///   2. SAME-HOUR-OF-DAY: any snapshot for the same instId where |Δt| ≤ 7 days AND
///      hour-of-day matches within ±1h. Captures the intraday spread pattern (Asia
///      open vs US close are systematically different) when same-day data is missing.
///   3. ANY snapshot for the symbol within the harvest window: median of available.
///   4. null: caller must fall back to defaults (returns to legacy behavior).
///
/// File format produced by OkxSymbolCostProfileProvider:
///   ts_utc,instId,bid,ask,mid,spread_bps
///
/// Memory: lazy-loads on first call, caches grouped by instId. ~7200 rows/day,
/// ~25 KB/day on disk; 90 days = ~2 MB in memory. Reload only triggered when
/// the file grows by >10% since last load (cheap mtime + size check).
/// </summary>
public sealed class HistoricalSpreadReader
{
    private readonly ILogger<HistoricalSpreadReader> _logger;
    private readonly string _snapshotPath;
    private readonly object _loadLock = new();
    private Dictionary<string, List<SpreadRow>> _byInstId = new(StringComparer.OrdinalIgnoreCase);
    private long _lastLoadedSize = -1;
    private DateTime _lastLoadedUtc = DateTime.MinValue;

    public HistoricalSpreadReader(ILogger<HistoricalSpreadReader> logger, IConfiguration cfg)
    {
        _logger = logger;
        var configured = cfg["Persistence:SpreadSnapshotPath"];
        _snapshotPath = !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant",
                "spread_snapshots.csv");
    }

    /// <summary>
    /// Returns the best-available spread (in bps) for <paramref name="instId"/> at
    /// <paramref name="atUtc"/>, or <c>null</c> if the dataset has no usable record.
    /// Caller can use this to construct an accurate <c>SymbolCostProfile.SpreadBps</c>
    /// for historical bars.
    /// </summary>
    public decimal? Lookup(string instId, DateTime atUtc)
    {
        EnsureLoaded();
        if (!_byInstId.TryGetValue(instId, out var rows) || rows.Count == 0) return null;

        // Tier 1: exact-day match
        var dayWindow = TimeSpan.FromHours(24);
        SpreadRow? best = null;
        TimeSpan bestDelta = TimeSpan.MaxValue;
        foreach (var r in rows)
        {
            var delta = (r.TsUtc - atUtc).Duration();
            if (delta < dayWindow && delta < bestDelta) { best = r; bestDelta = delta; }
        }
        if (best.HasValue) return best.Value.SpreadBps;

        // Tier 2: same-hour-of-day within 7d
        var weekWindow = TimeSpan.FromDays(7);
        int targetHour = atUtc.Hour;
        decimal sum = 0; int count = 0;
        foreach (var r in rows)
        {
            var delta = (r.TsUtc - atUtc).Duration();
            if (delta > weekWindow) continue;
            var hourDiff = Math.Abs(r.TsUtc.Hour - targetHour);
            if (hourDiff > 1 && hourDiff < 23) continue; // wrap-around (23h diff = 1h on the clock)
            sum += r.SpreadBps;
            count++;
        }
        if (count > 0) return sum / count;

        // Tier 3: median of all rows for this symbol
        var sorted = rows.Select(r => r.SpreadBps).OrderBy(x => x).ToList();
        return sorted.Count == 0 ? (decimal?)null : sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Diagnostic: rows currently loaded per instId. Useful for the analyze-edge
    /// CLI / dashboard to show "spread coverage" before an operator trusts a backtest.
    /// </summary>
    public IReadOnlyDictionary<string, int> CoverageSnapshot()
    {
        EnsureLoaded();
        return _byInstId.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureLoaded()
    {
        if (!File.Exists(_snapshotPath))
        {
            // File hasn't been written yet — provider hasn't fetched anything.
            // Empty dict is a stable state.
            return;
        }

        var info = new FileInfo(_snapshotPath);
        // Reload trigger: first load, or file grew by >10% since last load, or 1h elapsed.
        var growth = _lastLoadedSize <= 0 ? double.PositiveInfinity : (double)(info.Length - _lastLoadedSize) / _lastLoadedSize;
        var aged = (DateTime.UtcNow - _lastLoadedUtc) > TimeSpan.FromHours(1);
        if (_lastLoadedSize > 0 && growth < 0.10 && !aged) return;

        lock (_loadLock)
        {
            // Re-check inside the lock — avoid double-load.
            info.Refresh();
            growth = _lastLoadedSize <= 0 ? double.PositiveInfinity : (double)(info.Length - _lastLoadedSize) / _lastLoadedSize;
            aged = (DateTime.UtcNow - _lastLoadedUtc) > TimeSpan.FromHours(1);
            if (_lastLoadedSize > 0 && growth < 0.10 && !aged) return;

            try
            {
                var grouped = new Dictionary<string, List<SpreadRow>>(StringComparer.OrdinalIgnoreCase);
                using var reader = new StreamReader(_snapshotPath);
                var header = reader.ReadLine(); // skip header
                string? line;
                int parsed = 0, skipped = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Split(',');
                    if (parts.Length < 6) { skipped++; continue; }
                    if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) { skipped++; continue; }
                    var inst = parts[1];
                    if (!decimal.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var sp)) { skipped++; continue; }
                    if (!grouped.TryGetValue(inst, out var list)) { list = new List<SpreadRow>(); grouped[inst] = list; }
                    list.Add(new SpreadRow(ts, sp));
                    parsed++;
                }
                _byInstId = grouped;
                _lastLoadedSize = info.Length;
                _lastLoadedUtc = DateTime.UtcNow;
                _logger.LogInformation(
                    "HistoricalSpreadReader loaded: {Symbols} symbols, {Rows} rows ({Skipped} skipped) from {Path} ({Bytes} bytes)",
                    grouped.Count, parsed, skipped, _snapshotPath, info.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HistoricalSpreadReader load failed for {Path}", _snapshotPath);
            }
        }
    }

    private readonly record struct SpreadRow(DateTime TsUtc, decimal SpreadBps);
}
