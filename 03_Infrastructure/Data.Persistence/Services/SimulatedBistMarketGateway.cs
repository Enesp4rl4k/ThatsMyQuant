using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class SimulatedBistMarketGateway : IBistMarketGateway
{
    private static readonly IReadOnlyList<string> Universe =
    [
        "THYAO", "GARAN", "AKBNK", "SISE", "EREGL", "TUPRS", "BIMAS", "ASELS", "KCHOL", "SAHOL"
    ];

    private readonly ILogger<SimulatedBistMarketGateway> _logger;

    public SimulatedBistMarketGateway(ILogger<SimulatedBistMarketGateway> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<string>> GetUniverseAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Universe);

    public Task<decimal> GetLivePriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching live BIST price for {Symbol} via simulated Borsa gateway.", symbol);
        return Task.FromResult(symbol.ToUpperInvariant() switch
        {
            "THYAO" => 284.20m,
            "GARAN" => 52.80m,
            "AKBNK" => 46.35m,
            "SISE" => 56.10m,
            "EREGL" => 49.90m,
            _ => 125.50m
        });
    }

    public Task<Dictionary<string, string>> GetTechnicalAnalysisAsync(string symbol, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching BIST technical analysis for {Symbol} via simulated Borsa gateway.", symbol);
        return Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RSI"] = "62.5",
            ["MACD"] = "Bullish",
            ["Trend"] = "Strong Up",
            ["LiquidityBucket"] = "Tier1"
        });
    }

    public Task<List<string>> GetLatestNewsAsync(string symbol, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching BIST news for {Symbol} via simulated Borsa gateway.", symbol);
        return Task.FromResult(new List<string>
        {
            $"KAP: {symbol} published a new operational update.",
            $"Desk note: {symbol} remains in the active BIST momentum basket."
        });
    }

    public Task<List<Candle>> GetCandlesAsync(string symbol, int count = 100, string timeframe = "1d", CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating {Count} simulated {Timeframe} BIST candles for {Symbol}.", count, timeframe, symbol);

        var candles = new List<Candle>(count);
        var random = new Random(HashCode.Combine(symbol.ToUpperInvariant(), timeframe, count));
        var price = symbol.ToUpperInvariant() switch
        {
            "THYAO" => 280m,
            "GARAN" => 50m,
            "AKBNK" => 42m,
            "SISE" => 55m,
            "EREGL" => 48m,
            "KCHOL" => 160m,
            "TUPRS" => 180m,
            "BIMAS" => 340m,
            "ASELS" => 45m,
            "SAHOL" => 65m,
            _ => 100m
        };

        var step = timeframe.ToLowerInvariant() switch
        {
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "1h" => TimeSpan.FromHours(1),
            _ => TimeSpan.FromDays(1)
        };

        var cursor = DateTime.UtcNow - TimeSpan.FromTicks(step.Ticks * count);
        for (var i = 0; i < count; i++)
        {
            var drift = (decimal)((random.NextDouble() - 0.47d) * 0.018d);
            var open = price;
            var close = Math.Max(1m, open * (1m + drift));
            var high = Math.Max(open, close) * (1m + (decimal)random.NextDouble() * 0.01m);
            var low = Math.Min(open, close) * (1m - (decimal)random.NextDouble() * 0.01m);
            var volume = 3_500_000m * (decimal)(0.8d + random.NextDouble() * 0.6d);

            candles.Add(new Candle
            {
                Timestamp = cursor,
                Open = Math.Round(open, 2),
                High = Math.Round(high, 2),
                Low = Math.Round(low, 2),
                Close = Math.Round(close, 2),
                Volume = Math.Round(volume, 0)
            });

            cursor += step;
            price = close;
        }

        return Task.FromResult(candles);
    }
}
