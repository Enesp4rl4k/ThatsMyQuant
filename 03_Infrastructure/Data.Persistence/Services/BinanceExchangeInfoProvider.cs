using System.Net.Http;
using Microsoft.Extensions.Http;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class BinanceExchangeInfoProvider : IExchangeInfoProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BinanceExchangeInfoProvider> _logger;
    private readonly ConcurrentDictionary<string, SymbolInfo> _cache = new();
    private const string BaseUrl = "https://api.binance.com/api/v3/exchangeInfo";

    public BinanceExchangeInfoProvider(IHttpClientFactory httpClientFactory, ILogger<BinanceExchangeInfoProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Binance");
        _logger = logger;
    }

    public double GetTickSize(string symbol)
    {
        var normalized = NormalizeSymbol(symbol);
        return _cache.TryGetValue(normalized, out var info) ? info.TickSize : 0.01;
    }

    public double GetLotSize(string symbol)
    {
        var normalized = NormalizeSymbol(symbol);
        return _cache.TryGetValue(normalized, out var info) ? info.LotSize : 0.001;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Refreshing Binance ExchangeInfo cache...");
            var response = await _httpClient.GetFromJsonAsync<ExchangeInfoResponse>(BaseUrl, ct);
            if (response?.Symbols == null) return;

            foreach (var s in response.Symbols)
            {
                double tickSize = 0.01;
                double lotSize = 0.001;

                foreach (var filter in s.Filters)
                {
                    if (filter.FilterType == "PRICE_FILTER" && filter.TickSize != null)
                        tickSize = double.Parse(filter.TickSize, System.Globalization.CultureInfo.InvariantCulture);
                    
                    if (filter.FilterType == "LOT_SIZE" && filter.StepSize != null)
                        lotSize = double.Parse(filter.StepSize, System.Globalization.CultureInfo.InvariantCulture);
                }

                _cache[s.Symbol] = new SymbolInfo(tickSize, lotSize);
            }
            _logger.LogInformation("Binance ExchangeInfo cache initialized with {Count} symbols.", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Binance ExchangeInfo provider.");
        }
    }

    private static string NormalizeSymbol(string symbol) => symbol.Replace("-", "").ToUpperInvariant();

    private record SymbolInfo(double TickSize, double LotSize);
    
    private class ExchangeInfoResponse
    {
        public List<SymbolDetail>? Symbols { get; set; }
    }

    private class SymbolDetail
    {
        public string Symbol { get; set; } = string.Empty;
        public List<FilterDetail> Filters { get; set; } = new();
    }

    private class FilterDetail
    {
        public string FilterType { get; set; } = string.Empty;
        public string? TickSize { get; set; }
        public string? StepSize { get; set; }
    }
}
