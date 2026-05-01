using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Real per-symbol cost profile provider. Pulls current L1 bid/ask from OKX public ticker
/// (no auth required) and looks up fee tier from <see cref="OkxFeeSchedule"/> by instrument
/// type. Result cached for <see cref="CacheTtl"/> so a multi-symbol backtest batch doesn't
/// pound the endpoint.
///
/// 2026-04-30: in addition to caching, every successful spread fetch is appended to
/// <c>spread_snapshots.csv</c> in the persistence directory. This builds an organic
/// historical spread series which a future backtest layer can read with timestamp-aware
/// nearest-neighbor lookup, replacing the "today's spread applied to all of history"
/// hack. Until the reader ships, this just accumulates data — cheap, append-only, no
/// behavioral change for existing consumers.
/// </summary>
public sealed class OkxSymbolCostProfileProvider : ISymbolCostProfileProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OkxSymbolCostProfileProvider> _logger;
    private readonly HistoricalSpreadReader? _historicalReader;
    private readonly ConcurrentDictionary<string, (SymbolCostProfile Profile, DateTime Utc)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _snapshotPath;
    private readonly object _snapshotWriteLock = new();
    private bool _snapshotHeaderWritten;

    public OkxSymbolCostProfileProvider(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<OkxSymbolCostProfileProvider> logger,
        HistoricalSpreadReader? historicalReader = null)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
        _logger = logger;
        _historicalReader = historicalReader;

        // Same persistence dir as paper_portfolio.json by default; can be overridden via
        // Persistence:SpreadSnapshotPath. Append-only — file grows ~1 row per (symbol, 5min)
        // hit, well under 100KB/day even for a 25-symbol watchlist. Rotated externally.
        var configured = _configuration["Persistence:SpreadSnapshotPath"];
        _snapshotPath = !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant",
                "spread_snapshots.csv");
        try { Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not create spread snapshot dir."); }
    }

    public async Task<SymbolCostProfile?> GetAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        var instId = ResolveInstId(symbol);
        if (_cache.TryGetValue(instId, out var cached) && DateTime.UtcNow - cached.Utc < CacheTtl)
            return cached.Profile;

        var instType = OkxFeeSchedule.ClassifyInstrument(instId);
        var fees = OkxFeeSchedule.Resolve(instType);

        decimal? spreadBps = await FetchSpreadBpsAsync(instId, ct).ConfigureAwait(false);
        if (spreadBps is null)
        {
            _logger.LogDebug("OKX spread lookup failed for {InstId}; profile unavailable.", instId);
            return null;
        }

        var profile = new SymbolCostProfile(
            Symbol: symbol,
            TakerBps: fees.TakerBps,
            MakerBps: fees.MakerBps,
            SpreadBps: spreadBps.Value,
            CapturedAtUtc: DateTime.UtcNow);

        _cache[instId] = (profile, DateTime.UtcNow);
        _logger.LogInformation(
            "OKX cost profile {InstId}: taker={Taker}bps maker={Maker}bps spread={Spread:F2}bps ({InstType})",
            instId, fees.TakerBps, fees.MakerBps, spreadBps.Value, instType);
        return profile;
    }

    /// <summary>
    /// Historical-aware variant. Looks up the spread snapshot closest to
    /// <paramref name="atUtc"/> via <see cref="HistoricalSpreadReader"/> and returns a
    /// profile with that spread. Falls back to <see cref="GetAsync"/> semantics (today's
    /// spread) when the reader is unavailable or the dataset has no usable record for
    /// the timestamp. Fee schedule is timestamp-independent and pulled from
    /// <see cref="OkxFeeSchedule"/> as before.
    /// </summary>
    public async Task<SymbolCostProfile?> GetAsAtAsync(string symbol, DateTime atUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        var instId = ResolveInstId(symbol);
        if (_historicalReader is not null)
        {
            var historicalSpread = _historicalReader.Lookup(instId, atUtc);
            if (historicalSpread.HasValue)
            {
                var instType = OkxFeeSchedule.ClassifyInstrument(instId);
                var fees = OkxFeeSchedule.Resolve(instType);
                return new SymbolCostProfile(
                    Symbol: symbol,
                    TakerBps: fees.TakerBps,
                    MakerBps: fees.MakerBps,
                    SpreadBps: historicalSpread.Value,
                    CapturedAtUtc: atUtc);
            }
        }
        // Fallback: legacy "today's spread" path. Logs once per unique (symbol, day)
        // so operators see the gap when historical coverage is thin.
        return await GetAsync(symbol, ct).ConfigureAwait(false);
    }

    private async Task<decimal?> FetchSpreadBpsAsync(string instId, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("OkxPublic");
            var uri = $"/api/v5/market/ticker?instId={Uri.EscapeDataString(instId)}";
            using var response = await client.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.GetProperty("code").GetString() != "0") return null;

            var data = root.GetProperty("data");
            if (data.GetArrayLength() == 0) return null;

            var row = data[0];
            if (!row.TryGetProperty("bidPx", out var bidEl) || !row.TryGetProperty("askPx", out var askEl))
                return null;

            if (!TryDec(bidEl, out var bid) || !TryDec(askEl, out var ask) || bid <= 0m || ask <= 0m)
                return null;
            if (ask < bid) (bid, ask) = (ask, bid);

            var mid = (bid + ask) / 2m;
            if (mid <= 0m) return null;

            var spreadBps = (ask - bid) / mid * 10_000m;
            // Append to historical series — best-effort, never throws upstream.
            TryAppendSnapshot(instId, bid, ask, mid, spreadBps);
            return spreadBps;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OKX ticker fetch threw for {InstId}", instId);
            return null;
        }
    }

    /// <summary>
    /// Normalize an internal symbol ("BTCUSDT" / "BTC-USDT") to an OKX instId, appending "-SWAP"
    /// only if the configured exchange mode is swap and the caller hasn't already done so.
    /// </summary>
    private string ResolveInstId(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        // Accept "BTCUSDT" → "BTC-USDT"
        if (!normalized.Contains('-'))
        {
            // naive split: assume quote is last 3-4 chars matching USDT/USDC/USD/BUSD
            foreach (var quote in new[] { "USDT", "USDC", "BUSD", "USD" })
            {
                if (normalized.EndsWith(quote) && normalized.Length > quote.Length)
                {
                    normalized = $"{normalized[..^quote.Length]}-{quote}";
                    break;
                }
            }
        }

        var mode = _configuration["TMQ_EXCHANGE_MODE"] ?? _configuration["ExchangeMode"] ?? string.Empty;
        var isSwap = mode.Contains("SWAP", StringComparison.OrdinalIgnoreCase);
        if (isSwap && !normalized.EndsWith("-SWAP", StringComparison.OrdinalIgnoreCase))
            normalized = $"{normalized}-SWAP";
        return normalized;
    }

    /// <summary>
    /// Append-only writer for the spread time series. Format: ISO-8601 UTC, instId, bid,
    /// ask, mid, spread_bps. Locked because multiple symbol fetches may complete
    /// concurrently. Errors swallowed — telemetry must not break the cost profile path.
    /// </summary>
    private void TryAppendSnapshot(string instId, decimal bid, decimal ask, decimal mid, decimal spreadBps)
    {
        try
        {
            lock (_snapshotWriteLock)
            {
                var needHeader = !_snapshotHeaderWritten && !File.Exists(_snapshotPath);
                using var sw = new StreamWriter(_snapshotPath, append: true);
                if (needHeader) sw.WriteLine("ts_utc,instId,bid,ask,mid,spread_bps");
                _snapshotHeaderWritten = true;
                sw.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:O},{1},{2},{3},{4},{5:F4}",
                    DateTime.UtcNow, instId, bid, ask, mid, spreadBps));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Spread snapshot append failed for {InstId}", instId);
        }
    }

    private static bool TryDec(JsonElement el, out decimal value)
    {
        value = 0m;
        return el.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            JsonValueKind.Number => el.TryGetDecimal(out value),
            _ => false
        };
    }
}
