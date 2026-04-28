using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Real OKX funding rate provider. Pulls
/// <c>/api/v5/public/funding-rate-history?instId=&amp;before=&amp;limit=100</c> and paginates
/// backwards to build a full history per symbol, then caches results to JSON on disk
/// (cache_dir/funding/{instId}.json). Subsequent backtests load from disk instantly.
///
/// The endpoint is public (no auth), rate-limited to 10 req / 2s per IP. We throttle with a
/// short delay between pages. Data persists across 3-month rolling windows.
///
/// <see cref="GetRate"/> is synchronous (matches the interface used deep in BacktestEngine
/// hot-loop). On first call for a symbol it will trigger an ensure-loaded; we block the first
/// call but subsequent calls are pure in-memory lookups. Call <see cref="PrefetchAsync"/>
/// from a warm-up phase to avoid blocking the hot loop.
/// </summary>
public sealed class OkxFundingRateProvider : IFundingRateProvider
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OkxFundingRateProvider> _logger;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Series(long[] Timestamps, decimal[] Rates);

    public OkxFundingRateProvider(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<OkxFundingRateProvider> logger)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
        _logger = logger;

        var baseDir = configuration["Backtest:Cache:Root"] ?? Path.Combine(AppContext.BaseDirectory, "cache");
        _cacheDir = Path.Combine(baseDir, "funding");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <inheritdoc />
    public decimal GetRate(string symbol, DateTime asOfUtc)
    {
        var instId = Normalize(symbol);
        if (!_series.TryGetValue(instId, out var s))
        {
            // Block-load once; cached thereafter. Keeps the interface sync for hot-loop callers.
            try { EnsureLoadedAsync(instId, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Funding ensure-load failed for {InstId}", instId); return 0m; }
            if (!_series.TryGetValue(instId, out s)) return 0m;
        }

        if (s.Timestamps.Length == 0) return 0m;
        var ts = new DateTimeOffset(asOfUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        // Binary-search latest funding observation ≤ asOfUtc.
        int lo = 0, hi = s.Timestamps.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (s.Timestamps[mid] <= ts) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found < 0 ? 0m : s.Rates[found];
    }

    /// <inheritdoc />
    public IReadOnlyList<(DateTime Timestamp, decimal Rate)> GetHistory(string symbol, DateTime fromUtc, DateTime toUtc)
    {
        var instId = Normalize(symbol);
        if (!_series.TryGetValue(instId, out var s))
        {
            try { EnsureLoadedAsync(instId, CancellationToken.None, fromUtc).GetAwaiter().GetResult(); }
            catch { return Array.Empty<(DateTime, decimal)>(); }
            if (!_series.TryGetValue(instId, out s)) return Array.Empty<(DateTime, decimal)>();
        }
        if (s.Timestamps.Length == 0) return Array.Empty<(DateTime, decimal)>();

        var fromMs = new DateTimeOffset(fromUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(toUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var list = new List<(DateTime, decimal)>();
        for (int i = 0; i < s.Timestamps.Length; i++)
        {
            var ts = s.Timestamps[i];
            if (ts < fromMs || ts > toMs) continue;
            list.Add((DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime, s.Rates[i]));
        }
        return list;
    }

    /// <summary>Warm the cache for a set of symbols covering a date range before running backtests.</summary>
    public async Task PrefetchAsync(IEnumerable<string> symbols, DateTime fromUtc, CancellationToken ct = default)
    {
        foreach (var sym in symbols)
        {
            if (ct.IsCancellationRequested) break;
            var instId = Normalize(sym);
            await EnsureLoadedAsync(instId, ct, fromUtc).ConfigureAwait(false);
        }
    }

    private async Task EnsureLoadedAsync(string instId, CancellationToken ct, DateTime? fromUtc = null)
    {
        if (_series.ContainsKey(instId)) return;

        var gate = _locks.GetOrAdd(instId, _ => new object());
        Series? series;
        lock (gate) { _series.TryGetValue(instId, out series); }
        if (series != null) return;

        // 1) Try disk cache.
        var diskPath = Path.Combine(_cacheDir, $"{instId}.json");
        if (File.Exists(diskPath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(diskPath, ct).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<PersistDto>(raw);
                if (loaded is not null && loaded.Ts.Length == loaded.Rate.Length && loaded.Ts.Length > 0)
                {
                    var s = new Series(loaded.Ts, loaded.Rate);
                    _series[instId] = s;
                    _logger.LogInformation("Funding cache hit {InstId}: {Count} observations ({From:yyyy-MM-dd} → {To:yyyy-MM-dd})",
                        instId, s.Timestamps.Length,
                        DateTimeOffset.FromUnixTimeMilliseconds(s.Timestamps[0]).UtcDateTime,
                        DateTimeOffset.FromUnixTimeMilliseconds(s.Timestamps[^1]).UtcDateTime);
                    return;
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Funding cache read failed for {InstId}; refetching", instId); }
        }

        // 2) Fetch from OKX. Paginate from newest → oldest via `after` param.
        // OKX semantics: `after` = records EARLIER than timestamp (i.e. older), `before` = NEWER.
        // Bug fix 2026-04-24: prior code used `before` which returns newer data → loop never
        // progressed backward and API returned empty. Use `after` for backward pagination.
        var all = new List<(long Ts, decimal Rate)>();
        long? afterTs = null;
        long oldestMs = fromUtc.HasValue
            ? new DateTimeOffset(fromUtc.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()
            : new DateTimeOffset(DateTime.UtcNow.AddDays(-400), TimeSpan.Zero).ToUnixTimeMilliseconds();

        var client = _httpFactory.CreateClient("OkxPublic");
        const int pageLimit = 100;
        long? previousMinTs = null;
        int stalePages = 0;
        for (int page = 0; page < 120; page++)  // hard cap to prevent infinite loop
        {
            if (ct.IsCancellationRequested) break;

            var uri = $"/api/v5/public/funding-rate-history?instId={Uri.EscapeDataString(instId)}&limit={pageLimit}";
            if (afterTs.HasValue) uri += $"&after={afterTs.Value}";

            try
            {
                using var response = await client.GetAsync(uri, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) break;

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var root = doc.RootElement;
                if (root.GetProperty("code").GetString() != "0") break;

                var rows = root.GetProperty("data");
                if (rows.GetArrayLength() == 0) break;

                long? minTsThisPage = null;
                int rowsAdded = 0;
                foreach (var row in rows.EnumerateArray())
                {
                    if (!row.TryGetProperty("fundingTime", out var tsEl) ||
                        !row.TryGetProperty("fundingRate", out var rateEl)) continue;
                    if (!long.TryParse(tsEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ts)) continue;
                    if (!decimal.TryParse(rateEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var rate)) continue;

                    all.Add((ts, rate));
                    rowsAdded++;
                    minTsThisPage = minTsThisPage.HasValue ? Math.Min(minTsThisPage.Value, ts) : ts;
                }

                if (minTsThisPage is null) break;

                // Stale-page guard: if OKX returns rows but the minimum timestamp doesn't
                // advance backward, the pagination cursor is stuck. Tolerate one stuck page
                // (can happen on boundary duplicates) then abort to avoid a spin loop even
                // under the 120-page cap.
                if (previousMinTs.HasValue && minTsThisPage.Value >= previousMinTs.Value)
                {
                    stalePages++;
                    if (stalePages >= 2)
                    {
                        _logger.LogWarning(
                            "Funding pagination stall for {InstId}: cursor stuck at {Ts} ({Rows} rows, {Page} pages fetched).",
                            instId, minTsThisPage.Value, rowsAdded, page + 1);
                        break;
                    }
                }
                else
                {
                    stalePages = 0;
                }

                if (minTsThisPage.Value <= oldestMs) break;
                previousMinTs = minTsThisPage;
                afterTs = minTsThisPage.Value;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Funding page fetch failed for {InstId} at after={After}", instId, afterTs);
                break;
            }

            // Respect rate limits (10 req / 2s per IP).
            await Task.Delay(TimeSpan.FromMilliseconds(220), ct).ConfigureAwait(false);
        }

        if (all.Count == 0)
        {
            _logger.LogWarning("No funding history returned for {InstId}; falling back to 0.", instId);
            _series[instId] = new Series(Array.Empty<long>(), Array.Empty<decimal>());
            return;
        }

        all.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        // Dedup same-timestamp rows that the overlap between pages can produce.
        var ts_ = new List<long>(all.Count);
        var rt_ = new List<decimal>(all.Count);
        long last = long.MinValue;
        foreach (var (t, r) in all)
        {
            if (t == last) continue;
            ts_.Add(t); rt_.Add(r); last = t;
        }

        var finalSeries = new Series(ts_.ToArray(), rt_.ToArray());
        _series[instId] = finalSeries;

        try
        {
            var dto = new PersistDto(finalSeries.Timestamps, finalSeries.Rates);
            await File.WriteAllTextAsync(diskPath, JsonSerializer.Serialize(dto), ct).ConfigureAwait(false);
            _logger.LogInformation("Funding history {InstId}: {Count} obs cached → {Path}", instId, finalSeries.Timestamps.Length, diskPath);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Funding cache write failed for {InstId}", instId); }
    }

    private static string Normalize(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return symbol;
        var s = symbol.Trim().ToUpperInvariant();
        if (!s.Contains('-'))
        {
            foreach (var quote in new[] { "USDT", "USDC", "BUSD", "USD" })
                if (s.EndsWith(quote) && s.Length > quote.Length)
                { s = $"{s[..^quote.Length]}-{quote}"; break; }
        }
        // Funding is perp-only; force the -SWAP suffix.
        if (!s.EndsWith("-SWAP", StringComparison.OrdinalIgnoreCase)) s = $"{s}-SWAP";
        return s;
    }

    private sealed record PersistDto(long[] Ts, decimal[] Rate);
}
