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
/// Limitation: OKX public API does not expose historical L1 books for free. A backtest run
/// started today gets *today's* spread applied to every bar of the historical series. That's
/// still dramatically better than flat 3bps for every symbol — typical BTC-USDT-SWAP L1 is
/// ~1-3 bps but SOL ~3-6, DOGE ~5-10, small-caps 10-30+. If you need per-bar historical
/// spreads you must pipe in tick-level book snapshots (paid data / self-collected).
/// </summary>
public sealed class OkxSymbolCostProfileProvider : ISymbolCostProfileProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OkxSymbolCostProfileProvider> _logger;
    private readonly ConcurrentDictionary<string, (SymbolCostProfile Profile, DateTime Utc)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public OkxSymbolCostProfileProvider(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<OkxSymbolCostProfileProvider> logger)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
        _logger = logger;
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

            return (ask - bid) / mid * 10_000m; // spread in bps
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
