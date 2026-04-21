using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Implementation of IHistoricalMarketDataService for OKX API V5.
/// Focuses on historical candle retrieval with automatic pagination.
/// </summary>
public sealed class OkxHistoricalDataService : IHistoricalMarketDataService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OkxHistoricalDataService> _logger;
    private const string BaseUrl = "https://www.okx.com";
    private const int MaxRequestRetries = 5;

    public OkxHistoricalDataService(HttpClient httpClient, ILogger<OkxHistoricalDataService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<Candle>> GetHistoricalCandlesAsync(
        string symbol, 
        string timeframe, 
        DateTime start, 
        DateTime end, 
        CancellationToken ct = default)
    {
        var okxSymbol = NormalizeToOkx(symbol);
        var candles = new List<Candle>();
        
        // OKX V5 uses 'after' (older than) timestamp for pagination.
        // We start with the 'end' date and work backwards to 'start'.
        long after = new DateTimeOffset(end).ToUnixTimeMilliseconds();
        long startMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();

        _logger.LogInformation("Starting historical data fetch for {Symbol} ({Timeframe}) from {Start} to {End}", 
            symbol, timeframe, start, end);

        bool hasMore = true;
        while (hasMore && !ct.IsCancellationRequested)
        {
            // Endpoint: /api/v5/market/history-candles
            // Parameters: instId, after, bar, limit (default 100, max 100)
            var url = $"{BaseUrl}/api/v5/market/history-candles?instId={okxSymbol}&bar={MapTimeframe(timeframe)}&after={after}&limit=100";

            var response = await SendWithRetryAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("OKX API Error: {Error}", error);
                break;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            if (data.GetArrayLength() == 0)
            {
                hasMore = false;
                break;
            }

            foreach (var item in data.EnumerateArray())
            {
                // OKX Candle Format: [ts, o, h, l, c, vol, volCcy, volCcyQuote, confirm]
                var tsRaw = item[0].GetString() ?? "0";
                var ts = long.Parse(tsRaw);

                if (ts < startMs)
                {
                    hasMore = false;
                    break;
                }

                candles.Add(new Candle
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime,
                    Open = decimal.Parse(item[1].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    High = decimal.Parse(item[2].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    Low = decimal.Parse(item[3].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    Close = decimal.Parse(item[4].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    Volume = decimal.Parse(item[5].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
                });

                after = ts;
            }

            _logger.LogDebug("Fetched {Count} candles. Reached timestamp: {Ts}", candles.Count, after);
            
            // Rate limit safety
            await Task.Delay(100, ct); 
        }

        // Return sorted by time ascending
        return candles.OrderBy(c => c.Timestamp).ToList();
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxRequestRetries; attempt++)
        {
            try
            {
                var response = await _httpClient.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if ((int)response.StatusCode == 429 && attempt < MaxRequestRetries)
                {
                    var delayMs = (int)Math.Pow(2, attempt) * 300;
                    _logger.LogWarning("OKX rate limit hit (429). Retry {Attempt}/{Max} in {DelayMs}ms.", attempt, MaxRequestRetries, delayMs);
                    await Task.Delay(delayMs, ct);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex) when (attempt < MaxRequestRetries)
            {
                var delayMs = (int)Math.Pow(2, attempt) * 300;
                _logger.LogWarning(ex, "HTTP failure while fetching OKX candles. Retry {Attempt}/{Max} in {DelayMs}ms.", attempt, MaxRequestRetries, delayMs);
                await Task.Delay(delayMs, ct);
            }
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"msg\":\"Too Many Requests after retries\",\"code\":\"50011\"}")
        };
    }

    private static string NormalizeToOkx(string symbol)
    {
        if (symbol.Contains('-')) return symbol;
        // Simplified normalization: assumes USDT pair if no hyphen
        return symbol.EndsWith("USDT") ? $"{symbol[..^4]}-USDT" : symbol;
    }

    private static string MapTimeframe(string internalTf)
    {
        return internalTf.ToLower() switch
        {
            "1m" => "1m",
            "5m" => "5m",
            "15m" => "15m",
            "1h" => "1H",
            "4h" => "4H",
            "1d" => "1D",
            _ => "1H"
        };
    }
}
