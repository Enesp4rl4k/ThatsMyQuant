using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

public class ExchangeRoutedMarketDataService : IMarketDataService
{
    private static readonly Uri BinanceBaseUri = new("https://api.binance.com");
    private static readonly Uri BinanceTestnetBaseUri = new("https://testnet.binance.vision");
    private static readonly Uri OkxBaseUri = new("https://www.okx.com");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly YahooFinanceMarketDataService _yahooFinanceMarketDataService;
    private readonly BorsaMarketDataService _borsaMarketDataService;
    private readonly ILogger<ExchangeRoutedMarketDataService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, (DateTime fetchedAt, List<Candle> candles)> _candleCache = new();
    private readonly ConcurrentDictionary<string, (DateTime fetchedAt, decimal price)> _priceCache = new();

    public ExchangeRoutedMarketDataService(
        IHttpClientFactory httpClientFactory,
        YahooFinanceMarketDataService yahooFinanceMarketDataService,
        BorsaMarketDataService borsaMarketDataService,
        IConfiguration configuration,
        ILogger<ExchangeRoutedMarketDataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _yahooFinanceMarketDataService = yahooFinanceMarketDataService;
        _borsaMarketDataService = borsaMarketDataService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<decimal> GetLivePriceAsync(string symbol, string market = "BIST")
    {
        if (!market.Equals("Crypto", StringComparison.OrdinalIgnoreCase))
        {
            return await _yahooFinanceMarketDataService.GetLivePriceAsync(symbol, market);
        }

        var cacheKey = $"{ResolveExchangeMode()}::{NormalizeCryptoSymbol(symbol)}::price";
        if (_priceCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.fetchedAt < TimeSpan.FromSeconds(20))
        {
            return cached.price;
        }

        decimal price;
        var exchangeMode = ResolveExchangeMode();
        if (exchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase))
        {
            price = await TryGetOkxPriceAsync(symbol);
            if (price <= 0m)
            {
                price = await TryGetBinancePriceAsync(symbol, "Binance");
            }
        }
        else
        {
            price = await TryGetBinancePriceAsync(symbol, exchangeMode);
            if (price <= 0m)
            {
                price = await TryGetOkxPriceAsync(symbol);
            }
        }

        if (price <= 0m)
        {
            price = await _borsaMarketDataService.GetLivePriceAsync(symbol, market);
        }

        _priceCache[cacheKey] = (DateTime.UtcNow, price);
        return price;
    }

    public Task<Dictionary<string, string>> GetTechnicalAnalysisAsync(string symbol, string market = "BIST")
        => market.Equals("BIST", StringComparison.OrdinalIgnoreCase)
            ? _borsaMarketDataService.GetTechnicalAnalysisAsync(symbol, market)
            : _yahooFinanceMarketDataService.GetTechnicalAnalysisAsync(symbol, market);

    public Task<List<string>> GetLatestNewsAsync(string symbol)
        => _yahooFinanceMarketDataService.GetLatestNewsAsync(symbol);

    public async Task<List<Candle>> GetCandlesAsync(string symbol, int count = 100, string market = "BIST", string timeframe = "1d")
    {
        if (!market.Equals("Crypto", StringComparison.OrdinalIgnoreCase))
        {
            return await _yahooFinanceMarketDataService.GetCandlesAsync(symbol, count, market, timeframe);
        }

        var normalizedSymbol = NormalizeCryptoSymbol(symbol);
        var exchangeMode = ResolveExchangeMode();
        var cacheKey = $"{exchangeMode}::{normalizedSymbol}::{timeframe}::{count}";
        if (_candleCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.fetchedAt < TimeSpan.FromSeconds(30))
        {
            return cached.candles;
        }

        List<Candle> candles;
        if (exchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase))
        {
            candles = await TryGetOkxCandlesAsync(normalizedSymbol, count, timeframe);
            if (candles.Count == 0)
            {
                candles = await TryGetBinanceCandlesAsync(normalizedSymbol, count, timeframe, "Binance");
            }
        }
        else
        {
            candles = await TryGetBinanceCandlesAsync(normalizedSymbol, count, timeframe, exchangeMode);
            if (candles.Count == 0)
            {
                candles = await TryGetOkxCandlesAsync(normalizedSymbol, count, timeframe);
            }
        }

        if (candles.Count == 0)
        {
            _logger.LogWarning("Exchange candle fetch failed for {Symbol} ({Timeframe}). Falling back to local synthetic crypto feed.", normalizedSymbol, timeframe);
            candles = await _borsaMarketDataService.GetCandlesAsync(normalizedSymbol, count, market, timeframe);
        }

        _candleCache[cacheKey] = (DateTime.UtcNow, candles);
        return candles;
    }

    private async Task<List<Candle>> TryGetBinanceCandlesAsync(string symbol, int count, string timeframe, string exchangeMode)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var binanceSymbol = NormalizeToBinanceSymbol(symbol);
            var interval = MapToBinanceInterval(timeframe);
            var baseUri = exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase)
                ? BinanceTestnetBaseUri
                : BinanceBaseUri;
            var requestUri = new Uri(baseUri, $"/api/v3/klines?symbol={binanceSymbol}&interval={interval}&limit={Math.Clamp(count, 10, 1000)}");

            var response = await client.GetAsync(requestUri);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance candle request failed for {Symbol} with status {StatusCode}.", binanceSymbol, response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var candles = new List<Candle>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 6)
                {
                    continue;
                }

                candles.Add(new Candle
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry[0].GetInt64()).UtcDateTime,
                    Open = ParseDecimal(entry[1]),
                    High = ParseDecimal(entry[2]),
                    Low = ParseDecimal(entry[3]),
                    Close = ParseDecimal(entry[4]),
                    Volume = ParseDecimal(entry[5])
                });
            }

            return candles;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binance candle fetch failed for {Symbol}.", symbol);
            return [];
        }
    }

    private async Task<List<Candle>> TryGetOkxCandlesAsync(string symbol, int count, string timeframe)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var okxSymbol = NormalizeToOkxSymbol(symbol);
            var bar = MapToOkxBar(timeframe);
            var requestUri = new Uri(OkxBaseUri, $"/api/v5/market/candles?instId={okxSymbol}&bar={bar}&limit={Math.Clamp(count, 10, 300)}");

            var response = await client.GetAsync(requestUri);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OKX candle request failed for {Symbol} with status {StatusCode}.", okxSymbol, response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var candles = new List<Candle>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 6)
                {
                    continue;
                }

                if (!long.TryParse(entry[0].GetString(), out var timestampMs))
                {
                    continue;
                }

                candles.Add(new Candle
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime,
                    Open = ParseDecimal(entry[1]),
                    High = ParseDecimal(entry[2]),
                    Low = ParseDecimal(entry[3]),
                    Close = ParseDecimal(entry[4]),
                    Volume = ParseDecimal(entry[5])
                });
            }

            candles.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
            return candles.TakeLast(count).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX candle fetch failed for {Symbol}.", symbol);
            return [];
        }
    }

    private async Task<decimal> TryGetBinancePriceAsync(string symbol, string exchangeMode)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var binanceSymbol = NormalizeToBinanceSymbol(symbol);
            var baseUri = exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase)
                ? BinanceTestnetBaseUri
                : BinanceBaseUri;
            var requestUri = new Uri(baseUri, $"/api/v3/ticker/price?symbol={binanceSymbol}");

            var response = await client.GetAsync(requestUri);
            if (!response.IsSuccessStatusCode)
            {
                return 0m;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.TryGetProperty("price", out var priceElement)
                ? ParseDecimal(priceElement)
                : 0m;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binance price fetch failed for {Symbol}.", symbol);
            return 0m;
        }
    }

    private async Task<decimal> TryGetOkxPriceAsync(string symbol)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var okxSymbol = NormalizeToOkxSymbol(symbol);
            var requestUri = new Uri(OkxBaseUri, $"/api/v5/market/ticker?instId={okxSymbol}");

            var response = await client.GetAsync(requestUri);
            if (!response.IsSuccessStatusCode)
            {
                return 0m;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return 0m;
            }

            var ticker = data[0];
            return ticker.TryGetProperty("last", out var lastElement)
                ? ParseDecimal(lastElement)
                : 0m;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX price fetch failed for {Symbol}.", symbol);
            return 0m;
        }
    }

    private string ResolveExchangeMode()
    {
        var mode = SharedExchangeConfiguration.GetSelectedExchangeMode(_configuration);
        if (string.IsNullOrWhiteSpace(mode) || mode.Equals("Mock", StringComparison.OrdinalIgnoreCase))
        {
            return "Binance";
        }

        return mode.Trim();
    }

    private static string NormalizeCryptoSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return "BTCUSDT";
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        normalized = normalized.Replace("=X", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (normalized.Contains("/"))
        {
            normalized = normalized.Replace("/", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // Strip hyphens FIRST so OKX-style "BTC-USDT" canonicalises to "BTCUSDT" cleanly.
        // The previous "-USD"→"USDT" replace turned "BTC-USDT" into "BTCUSDTT" and then the
        // tail USDT-append produced "BTCUSDTTUSDT" which doesn't exist on any exchange and
        // forced a synthetic-data fallback for every symbol passed in OKX hyphenated form.
        normalized = SymbolNormalizer.ToCanonical(normalized);

        // Yahoo-style "BTC-USD" (sans T) → "BTCUSDT". After ToCanonical the hyphen is gone
        // so we look for the bare "USD" suffix.
        if (normalized.EndsWith("USD", StringComparison.OrdinalIgnoreCase) &&
            !normalized.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) &&
            !normalized.EndsWith("USDC", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized + "T";
        }

        return normalized.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}USDT";
    }

    private static string NormalizeToBinanceSymbol(string symbol)
        => NormalizeCryptoSymbol(symbol);

    private static string NormalizeToOkxSymbol(string symbol)
        => OkxExecutionService.NormalizeToOkx(NormalizeCryptoSymbol(symbol));

    private static string MapToBinanceInterval(string timeframe)
        => timeframe.ToLowerInvariant() switch
        {
            "1m" => "1m",
            "5m" => "5m",
            "15m" => "15m",
            "1h" => "1h",
            "4h" => "4h",
            _ => "1d"
        };

    private static string MapToOkxBar(string timeframe)
        => timeframe.ToLowerInvariant() switch
        {
            "1m" => "1m",
            "5m" => "5m",
            "15m" => "15m",
            "1h" => "1H",
            "4h" => "4H",
            _ => "1D"
        };

    private static decimal ParseDecimal(JsonElement element)
    {
        var raw = element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => element.GetString(),
            _ => null
        };

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }
}
