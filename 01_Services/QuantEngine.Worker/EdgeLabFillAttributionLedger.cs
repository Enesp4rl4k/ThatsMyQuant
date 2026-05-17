using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-16 per-strategy attribution patch — Quantlab integration]
///
/// PROBLEM: OKX positions-history endpoint (used by DbToPortfolioSync to mirror
/// closed positions) doesn't carry strategy attribution. Without attribution,
/// per-strategy digest collapses every EdgeLab fill into one bucket called
/// "OkxLive" — useless for track_realized.py OOS validation (needs per-strategy
/// realized vs expected Sharpe).
///
/// SOLUTION: append-only JSONL ledger written by EdgeLabSingleAssetHostedService
/// on every successful live fill. Each line records (timestampUtc, symbol,
/// strategy, side, qty, price, venueOrderId). DbToPortfolioSync queries this
/// ledger when mirroring closed positions: it matches an OKX-side position's
/// (symbol, openTimeUtc) against ledger entries within ±60s tolerance and
/// attributes the strategy tag. Falls back to "OkxLive" when no match.
///
/// File: %LOCALAPPDATA%\ThatsMyQuant\edge_lab_fill_attribution.jsonl
/// Retention: last 30 days (rotated daily — rotated lines are still searchable
/// for closed-mirror lookups since DbToPortfolioSync only looks back 7 days).
///
/// Thread-safe via internal lock on file append + in-memory recent-lookup cache.
/// </summary>
public sealed class EdgeLabFillAttributionLedger
{
    private readonly ILogger<EdgeLabFillAttributionLedger> _logger;
    private readonly string _ledgerPath;
    private readonly object _writeLock = new();

    // In-memory cache of recent fills (last 30 days, capped at 5000 entries to bound memory).
    // Loaded from disk at boot; appended on every write.
    private readonly List<FillAttributionEntry> _recentCache = new();
    private readonly object _cacheLock = new();
    private const int CacheMaxEntries = 5000;
    private static readonly TimeSpan CacheRetention = TimeSpan.FromDays(30);

    public EdgeLabFillAttributionLedger(
        ILogger<EdgeLabFillAttributionLedger> logger,
        IConfiguration cfg)
    {
        _logger = logger;
        var stateDir = Environment.ExpandEnvironmentVariables(
            cfg["StateDirectory"] ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant"));
        _ledgerPath = Path.Combine(stateDir, "edge_lab_fill_attribution.jsonl");

        try
        {
            Directory.CreateDirectory(stateDir);
            HydrateFromDisk();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EdgeLabFillAttributionLedger: hydrate at boot failed (non-fatal).");
        }
    }

    /// <summary>Append a successful EdgeLab live fill to the ledger.</summary>
    public void Append(
        DateTime timestampUtc,
        string symbol,
        string strategy,
        TradeSide side,
        decimal qty,
        decimal price,
        string? venueOrderId)
    {
        var entry = new FillAttributionEntry(
            timestampUtc, symbol, strategy, side.ToString(), qty, price, venueOrderId);

        try
        {
            var jsonLine = JsonSerializer.Serialize(entry);
            lock (_writeLock)
            {
                File.AppendAllText(_ledgerPath, jsonLine + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EdgeLabFillAttributionLedger: append failed for {Sym}/{Strat} — strategy attribution may be missing.",
                symbol, strategy);
        }

        // Update in-memory cache for fast lookup.
        lock (_cacheLock)
        {
            _recentCache.Add(entry);
            // Trim old entries
            var cutoff = DateTime.UtcNow - CacheRetention;
            _recentCache.RemoveAll(e => e.TimestampUtc < cutoff);
            // Trim cache size if exceeded
            if (_recentCache.Count > CacheMaxEntries)
            {
                _recentCache.RemoveRange(0, _recentCache.Count - CacheMaxEntries);
            }
        }
    }

    /// <summary>
    /// Lookup the most-likely strategy that produced a fill on `symbol` at `openTimeUtc`.
    /// Match window ±toleranceSeconds. Returns null when no match found.
    /// When multiple strategies match (e.g., eth_breakout_1h_long AND eth_breakout_4h_long
    /// both fired on ETH near the same time), returns the CLOSEST match by time.
    /// </summary>
    public string? LookupStrategy(string symbol, DateTime openTimeUtc, int toleranceSeconds = 60)
    {
        if (string.IsNullOrEmpty(symbol)) return null;

        lock (_cacheLock)
        {
            var matches = _recentCache
                .Where(e => string.Equals(e.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                .Where(e => Math.Abs((e.TimestampUtc - openTimeUtc).TotalSeconds) <= toleranceSeconds)
                .OrderBy(e => Math.Abs((e.TimestampUtc - openTimeUtc).TotalSeconds))
                .ToList();

            return matches.FirstOrDefault()?.Strategy;
        }
    }

    /// <summary>Diagnostic: how many entries currently in cache, with breakdown by strategy.</summary>
    public (int Total, IReadOnlyDictionary<string, int> ByStrategy) Snapshot()
    {
        lock (_cacheLock)
        {
            var byStrat = _recentCache
                .GroupBy(e => e.Strategy, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            return (_recentCache.Count, byStrat);
        }
    }

    private void HydrateFromDisk()
    {
        if (!File.Exists(_ledgerPath))
        {
            _logger.LogInformation("EdgeLabFillAttributionLedger: no prior ledger at {Path}; starting fresh.", _ledgerPath);
            return;
        }

        var cutoff = DateTime.UtcNow - CacheRetention;
        int loaded = 0, skipped = 0;
        foreach (var line in File.ReadLines(_ledgerPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<FillAttributionEntry>(line);
                if (entry is null) { skipped++; continue; }
                if (entry.TimestampUtc < cutoff) { skipped++; continue; }
                _recentCache.Add(entry);
                loaded++;
            }
            catch { skipped++; }
        }

        // Trim cache to max size (keep newest)
        if (_recentCache.Count > CacheMaxEntries)
        {
            _recentCache.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
            _recentCache.RemoveRange(0, _recentCache.Count - CacheMaxEntries);
        }

        _logger.LogInformation(
            "EdgeLabFillAttributionLedger hydrated: loaded={Loaded} skipped={Skipped} cache size={Cache}",
            loaded, skipped, _recentCache.Count);
    }

    private sealed record FillAttributionEntry(
        DateTime TimestampUtc,
        string Symbol,
        string Strategy,
        string Side,
        decimal Qty,
        decimal Price,
        string? VenueOrderId);
}
