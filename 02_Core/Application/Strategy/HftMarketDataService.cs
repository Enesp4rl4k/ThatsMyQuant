using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

public sealed class HftMarketDataService : IHftMarketDataService
{
    private readonly IMarketDataService _marketDataService;

    public HftMarketDataService(IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public Task<List<Candle>> GetCandlesAsync(Signal signal, string market, CancellationToken cancellationToken = default)
    {
        var timeframe = ResolveTimeframe(signal, market);
        var count = ResolveCount(market, timeframe);
        return _marketDataService.GetCandlesAsync(signal.Symbol, count, market, timeframe);
    }

    private static string ResolveTimeframe(Signal signal, string market)
    {
        if (signal.Metadata.TryGetValue("HftTimeframe", out var explicitHftTimeframe) &&
            !string.IsNullOrWhiteSpace(explicitHftTimeframe))
        {
            return explicitHftTimeframe;
        }

        if (signal.Metadata.TryGetValue("Timeframe", out var timeframe) &&
            !string.IsNullOrWhiteSpace(timeframe))
        {
            return timeframe;
        }

        return market switch
        {
            "BIST" => "5m",
            "Forex" => "1m",
            _ => "1m"
        };
    }

    private static int ResolveCount(string market, string timeframe)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
        {
            return timeframe == "5m" ? 72 : 90;
        }

        return timeframe == "1m" ? 120 : 90;
    }
}
