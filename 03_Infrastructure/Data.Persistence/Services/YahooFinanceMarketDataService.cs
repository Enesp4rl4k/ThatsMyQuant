using System.Net.Http;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Real market data service using Yahoo Finance v8 Chart API.
/// Provides live OHLCV candle data for BIST stocks (suffix .IS).
///
/// API: https://query1.finance.yahoo.com/v8/finance/chart/{SYMBOL}.IS
///
/// No API key required, but requires:
///   - Browser-like User-Agent header (Yahoo blocks bots without it)
///   - Rate limiting: ~2 requests/second max
///   - Fallback to BorsaMarketDataService if Yahoo is unavailable
/// </summary>
public class YahooFinanceMarketDataService : IMarketDataService
{
    private readonly HttpClient _httpClient;
    private readonly BorsaMarketDataService _fallback;
    private readonly ILogger<YahooFinanceMarketDataService> _logger;

    // Candle cache: request-key -> (timestamp, candles)
    private readonly ConcurrentDictionary<string, (DateTime FetchedAt, List<Candle> Candles)> _cache = new();
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public YahooFinanceMarketDataService(
        IHttpClientFactory httpClientFactory,
        BorsaMarketDataService fallback,
        ILogger<YahooFinanceMarketDataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Yahoo");
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<List<Candle>> GetCandlesAsync(string symbol, int count = 100, string market = "BIST", string timeframe = "1d")
    {
        var key = $"{symbol}_{count}_{market}_{timeframe}";

        // Return cached data if still fresh
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < _cacheDuration)
        {
            _logger.LogDebug("Cache hit for {Symbol}", symbol);
            return cached.Candles;
        }

        try
        {
            var yahooSymbol = symbol.ToUpper();
            
            // Format symbol based on market if not already formatted
            if (market.ToUpper() == "BIST" && !yahooSymbol.EndsWith(".IS"))
                yahooSymbol += ".IS";
            else if (market.ToUpper() == "CRYPTO" && !yahooSymbol.Contains("-"))
                yahooSymbol += "-USD";
            else if (market.ToUpper() == "FOREX" && !yahooSymbol.Contains("=") && !yahooSymbol.EndsWith("X"))
                yahooSymbol += "=X";

            var range = count <= 60 ? "3mo" : count <= 150 ? "6mo" : "1y";
            if (timeframe == "1h") range = (count > 150) ? "3mo" : "1mo";
            else if (timeframe == "5m") range = (count > 100) ? "1mo" : "5d";
            else if (timeframe == "15m") range = (count > 100) ? "1mo" : "5d";
            
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{yahooSymbol}?range={range}&interval={timeframe}";

            _logger.LogInformation("Fetching real OHLCV data: {Symbol} ({Market}) from Yahoo Finance ({Timeframe})...", yahooSymbol, market, timeframe);

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Yahoo Finance returned {Status} for {Symbol}. Using fallback.", response.StatusCode, symbol);
                return await _fallback.GetCandlesAsync(symbol, count, market, timeframe);
            }

            var json = await response.Content.ReadAsStringAsync();
            var candles = ParseYahooChartResponse(json, symbol);

            if (candles.Count == 0)
            {
                _logger.LogWarning("No candle data parsed for {Symbol}. Using fallback.", symbol);
                return await _fallback.GetCandlesAsync(symbol, count, market, timeframe);
            }

            // Take the most recent 'count' candles
            var result = candles.TakeLast(count).ToList();
        _cache[key] = (DateTime.UtcNow, result);

            _logger.LogInformation("✅ Fetched {Count} real candles for {Symbol}. Price range: {Low:F2} - {High:F2}",
                result.Count, symbol, result.Min(c => c.Low), result.Max(c => c.High));

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Yahoo Finance fetch failed for {Symbol}. Using fallback.", symbol);
            return await _fallback.GetCandlesAsync(symbol, count, market, timeframe);
        }
    }

    public async Task<decimal> GetLivePriceAsync(string symbol, string market = "BIST")
    {
        var candles = await GetCandlesAsync(symbol, 5, market);
        return candles.LastOrDefault()?.Close ?? 0;
    }

    public Task<Dictionary<string, string>> GetTechnicalAnalysisAsync(string symbol, string market = "BIST")
        => _fallback.GetTechnicalAnalysisAsync(symbol, market);

    public Task<List<string>> GetLatestNewsAsync(string symbol)
        => _fallback.GetLatestNewsAsync(symbol);

    // ─────────────────────────────────────────────────────────
    // JSON Parser for Yahoo Finance v8 Chart Response
    // ─────────────────────────────────────────────────────────
    private static List<Candle> ParseYahooChartResponse(string json, string symbol)
    {
        var candles = new List<Candle>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Navigate: chart.result[0]
            var result = root
                .GetProperty("chart")
                .GetProperty("result")[0];

            // Timestamps (Unix seconds)
            var timestamps = result.GetProperty("timestamp").EnumerateArray()
                .Select(t => DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()).UtcDateTime)
                .ToList();

            // OHLCV data is under indicators.quote[0]
            var quote = result
                .GetProperty("indicators")
                .GetProperty("quote")[0];

            var opens = quote.GetProperty("open").EnumerateArray().ToList();
            var highs = quote.GetProperty("high").EnumerateArray().ToList();
            var lows = quote.GetProperty("low").EnumerateArray().ToList();
            var closes = quote.GetProperty("close").EnumerateArray().ToList();
            var volumes = quote.GetProperty("volume").EnumerateArray().ToList();

            for (int i = 0; i < timestamps.Count && i < closes.Count; i++)
            {
                // Skip null entries (weekends/holidays sometimes produce nulls)
                if (closes[i].ValueKind == JsonValueKind.Null) continue;
                if (opens[i].ValueKind == JsonValueKind.Null) continue;

                candles.Add(new Candle
                {
                    Timestamp = timestamps[i],
                    Open = Math.Round((decimal)opens[i].GetDouble(), 2),
                    High = Math.Round((decimal)highs[i].GetDouble(), 2),
                    Low = Math.Round((decimal)lows[i].GetDouble(), 2),
                    Close = Math.Round((decimal)closes[i].GetDouble(), 2),
                    Volume = volumes[i].ValueKind != JsonValueKind.Null
                        ? Math.Round((decimal)volumes[i].GetDouble(), 0)
                        : 0
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Yahoo Parser] Failed for {symbol}: {ex.Message}");
        }

        return candles;
    }
}
