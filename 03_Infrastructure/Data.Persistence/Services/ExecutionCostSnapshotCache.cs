using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// In-memory TTL snapshot of venue taker fee + public bid/ask spread for paper / pre-trade costs.
/// </summary>
public sealed class ExecutionCostSnapshotCache : IExecutionCostSnapshotCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);

    private readonly IVenueTradeFeeProvider _feeProvider;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExecutionCostSnapshotCache> _logger;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(decimal? Fee, decimal? Spread, decimal? Slippage, DateTimeOffset Utc);

    public ExecutionCostSnapshotCache(
        IVenueTradeFeeProvider feeProvider,
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<ExecutionCostSnapshotCache> logger)
    {
        _feeProvider = feeProvider;
        _httpFactory = httpFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public decimal? GetTakerCommissionPercentPerSide(string symbol)
        => GetValid(symbol)?.Fee;

    public decimal? GetSpreadPercent(string symbol)
        => GetValid(symbol)?.Spread;

    public decimal? GetSlippagePercent(string symbol)
        => GetValid(symbol)?.Slippage;

    private Entry? GetValid(string symbol)
    {
        var key = NormalizeSymbol(symbol);
        if (!_entries.TryGetValue(key, out var e) || DateTimeOffset.UtcNow - e.Utc > Ttl)
            return null;
        return e;
    }

    public async Task RefreshAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var key = NormalizeSymbol(symbol);
        decimal? fee = null;
        decimal? spread = null;

        try
        {
            fee = await _feeProvider.GetTakerCommissionPercentPerSideAsync(symbol, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Execution cost: fee fetch failed for {Symbol}", symbol);
        }

        try
        {
            spread = await FetchSpreadPercentAsync(symbol, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Execution cost: spread fetch failed for {Symbol}", symbol);
        }

        decimal? slip = spread is > 0m ? spread.Value / 2m : null;
        _entries[key] = new Entry(fee, spread, slip, DateTimeOffset.UtcNow);
    }

    private async Task<decimal?> FetchSpreadPercentAsync(string symbol, CancellationToken cancellationToken)
    {
        var mode = SharedExchangeConfiguration.GetSelectedExchangeMode(_configuration);
        var isOkx = mode.Equals("OKX", StringComparison.OrdinalIgnoreCase) ||
                    mode.Equals("OKXSwap", StringComparison.OrdinalIgnoreCase);
        if (isOkx)
            return await FetchOkxSpreadAsync(symbol, cancellationToken);

        var isBinance = mode.StartsWith("Binance", StringComparison.OrdinalIgnoreCase);
        if (!isBinance)
            return null;

        return await FetchBinanceSpreadAsync(symbol, cancellationToken);
    }

    private async Task<decimal?> FetchBinanceSpreadAsync(string symbol, CancellationToken cancellationToken)
    {
        var sym = NormalizeSymbol(symbol);
        var client = _httpFactory.CreateClient("Binance");
        var uri = $"/api/v3/ticker/bookTicker?symbol={Uri.EscapeDataString(sym)}";
        using var response = await client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        if (!root.TryGetProperty("bidPrice", out var bidEl) || !root.TryGetProperty("askPrice", out var askEl))
            return null;

        if (!TryParseDecimal(bidEl, out var bid) || !TryParseDecimal(askEl, out var ask) || bid <= 0m || ask <= 0m)
            return null;

        if (ask < bid)
            (bid, ask) = (ask, bid);

        var mid = (bid + ask) / 2m;
        if (mid <= 0m)
            return null;

        return (ask - bid) / mid * 100m;
    }

    private async Task<decimal?> FetchOkxSpreadAsync(string symbol, CancellationToken cancellationToken)
    {
        var swapMode = (_configuration["TMQ_EXCHANGE_MODE"] ?? "").Contains("SWAP", StringComparison.OrdinalIgnoreCase);
        var instId = OkxExecutionService.NormalizeToOkx(symbol);
        if (swapMode && !instId.EndsWith("-SWAP", StringComparison.OrdinalIgnoreCase))
            instId = $"{instId}-SWAP";

        var client = _httpFactory.CreateClient("OkxPublic");
        var uri = $"/api/v5/market/ticker?instId={Uri.EscapeDataString(instId)}";
        using var response = await client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        if (root.GetProperty("code").GetString() != "0")
            return null;

        var data = root.GetProperty("data");
        if (data.GetArrayLength() == 0)
            return null;

        var row = data[0];
        if (!row.TryGetProperty("bidPx", out var bidEl) || !row.TryGetProperty("askPx", out var askEl))
            return null;

        if (!TryParseDecimal(bidEl, out var bid) || !TryParseDecimal(askEl, out var ask) || bid <= 0m || ask <= 0m)
            return null;

        if (ask < bid)
            (bid, ask) = (ask, bid);

        var mid = (bid + ask) / 2m;
        if (mid <= 0m)
            return null;

        return (ask - bid) / mid * 100m;
    }

    private static bool TryParseDecimal(JsonElement element, out decimal value)
    {
        value = 0m;
        return element.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                element.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out value),
            JsonValueKind.Number => element.TryGetDecimal(out value),
            _ => false
        };
    }

    private static string NormalizeSymbol(string symbol)
        => symbol.Replace("-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
}
