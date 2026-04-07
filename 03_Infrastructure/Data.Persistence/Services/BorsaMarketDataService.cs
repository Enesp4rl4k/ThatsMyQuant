using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

public class BorsaMarketDataService : IMarketDataService
{
    private readonly ILogger<BorsaMarketDataService> _logger;
    private readonly IBistMarketGateway _gateway;

    public BorsaMarketDataService(
        ILogger<BorsaMarketDataService> logger,
        IBistMarketGateway gateway)
    {
        _logger = logger;
        _gateway = gateway;
    }

    public Task<decimal> GetLivePriceAsync(string symbol, string market = "BIST")
    {
        if (market.Equals("BIST", StringComparison.OrdinalIgnoreCase))
        {
            return _gateway.GetLivePriceAsync(symbol);
        }

        _logger.LogInformation("Fetching live price for {Symbol} in {Market} via fallback market service.", symbol, market);
        return Task.FromResult(64000m);
    }

    public Task<Dictionary<string, string>> GetTechnicalAnalysisAsync(string symbol, string market = "BIST")
    {
        if (market.Equals("BIST", StringComparison.OrdinalIgnoreCase))
        {
            return _gateway.GetTechnicalAnalysisAsync(symbol);
        }

        _logger.LogInformation("Fetching technical analysis for {Symbol} in {Market} via fallback market service.", symbol, market);
        return Task.FromResult(new Dictionary<string, string>
        {
            ["RSI"] = "58.4",
            ["MACD"] = "Bullish",
            ["Trend"] = "Constructive"
        });
    }

    public Task<List<string>> GetLatestNewsAsync(string symbol)
        => _gateway.GetLatestNewsAsync(symbol);

    public Task<List<Candle>> GetCandlesAsync(string symbol, int count = 100, string market = "BIST", string timeframe = "1d")
    {
        if (market.Equals("BIST", StringComparison.OrdinalIgnoreCase))
        {
            return _gateway.GetCandlesAsync(symbol, count, timeframe);
        }

        _logger.LogInformation("Generating fallback {Count} simulated {Timeframe} candles for {Symbol} ({Market}).", count, timeframe, symbol, market);

        var candles = new List<Candle>(count);
        var random = Random.Shared;
        var price = market.Equals("Crypto", StringComparison.OrdinalIgnoreCase) ? 64000m : 1.12m;
        var step = timeframe.Equals("1h", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var cursor = DateTime.UtcNow - TimeSpan.FromTicks(step.Ticks * count);

        for (var i = 0; i < count; i++)
        {
            var change = (decimal)((random.NextDouble() - 0.48d) * 0.03d);
            var open = price;
            var close = Math.Max(0.0001m, open * (1m + change));
            var high = Math.Max(open, close) * (1m + (decimal)random.NextDouble() * 0.01m);
            var low = Math.Min(open, close) * (1m - (decimal)random.NextDouble() * 0.01m);

            candles.Add(new Candle
            {
                Timestamp = cursor,
                Open = Math.Round(open, 4),
                High = Math.Round(high, 4),
                Low = Math.Round(low, 4),
                Close = Math.Round(close, 4),
                Volume = Math.Round(100_000m * (decimal)(0.8d + random.NextDouble() * 0.4d), 0)
            });

            price = close;
            cursor += step;
        }

        return Task.FromResult(candles);
    }
}
