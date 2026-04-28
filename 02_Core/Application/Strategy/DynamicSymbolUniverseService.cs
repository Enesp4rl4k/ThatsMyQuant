using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

public sealed class DynamicSymbolUniverseService
{
    private static readonly IReadOnlyDictionary<string, string[]> DefaultUniverse = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["BIST"] = ["THYAO", "GARAN", "AKBNK", "SISE", "EREGL", "TUPRS", "BIMAS", "ASELS", "KCHOL", "SAHOL"],
        ["Crypto"] = ["BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT", "AVAXUSDT", "DOGEUSDT"],
        ["Forex"] = ["EURUSD", "GBPUSD", "USDTRY", "EURTRY", "USDJPY", "XAUUSD"]
    };

    private readonly IMarketDataService _marketDataService;
    private readonly IBistMarketGateway? _bistMarketGateway;
    private readonly ILogger<DynamicSymbolUniverseService> _logger;
    private readonly MarketUniverseOptions _universeOptions;

    public DynamicSymbolUniverseService(
        IMarketDataService marketDataService,
        ILogger<DynamicSymbolUniverseService> logger,
        IBistMarketGateway? bistMarketGateway = null,
        IOptions<MarketUniverseOptions>? universeOptions = null)
    {
        _marketDataService = marketDataService;
        _logger = logger;
        _bistMarketGateway = bistMarketGateway;
        _universeOptions = universeOptions?.Value ?? new MarketUniverseOptions();
    }

    public async Task<DynamicSymbolUniverseResult> BuildUniverseAsync(
        string market,
        IEnumerable<Trade>? recentTrades = null,
        int maxSymbols = 8,
        CancellationToken cancellationToken = default)
    {
        // Default to Crypto when unspecified — BIST as the old fallback made no sense
        // once we gated non-crypto markets behind feature flags.
        var normalizedMarket = string.IsNullOrWhiteSpace(market) ? "Crypto" : market;

        if (!_universeOptions.IsEnabled(normalizedMarket))
        {
            _logger.LogWarning(
                "Market {Market} is disabled via MarketUniverseOptions — returning empty universe. " +
                "Enable via Markets:Enable{Market}.", normalizedMarket, normalizedMarket);
            return new DynamicSymbolUniverseResult(normalizedMarket, 0, Array.Empty<UniverseCandidate>());
        }

        var candidates = await ResolveCandidatesAsync(normalizedMarket, cancellationToken);
        var expectancyLookup = BuildExpectancyLookup(recentTrades ?? []);
        var ranked = new List<UniverseCandidate>();

        foreach (var symbol in candidates)
        {
            var candles = await _marketDataService.GetCandlesAsync(symbol, count: 24, market: normalizedMarket, timeframe: normalizedMarket.Equals("BIST", StringComparison.OrdinalIgnoreCase) ? "1h" : "15m");
            if (candles.Count < 6)
            {
                continue;
            }

            var averageVolume = candles.Average(candle => candle.Volume);
            var averagePrice = candles.Average(candle => candle.Close);
            var momentum = averagePrice == 0m ? 0d : (double)((candles[^1].Close - candles[0].Open) / averagePrice);
            var averageRange = averagePrice == 0m
                ? 0d
                : candles.Average(candle => (double)((candle.High - candle.Low) / averagePrice));
            var expectancy = expectancyLookup.TryGetValue(symbol, out var edge) ? edge : 0m;

            var score = (averageRange * 35d) +
                        (momentum * 30d) +
                        (Math.Min((double)averageVolume / 10_000_000d, 1.5d) * 20d) +
                        ((double)expectancy * 15d);

            ranked.Add(new UniverseCandidate(
                symbol,
                Math.Round(score, 3),
                averageVolume,
                Math.Round((decimal)averageRange, 4),
                Math.Round((decimal)momentum, 4),
                expectancy,
                $"vol {averageRange:P1}, momentum {momentum:P1}, expectancy {expectancy:F2}"));
        }

        var selected = ranked
            .OrderByDescending(candidate => candidate.Score)
            .Take(Math.Max(1, maxSymbols))
            .ToList();

        _logger.LogInformation("Built dynamic universe for {Market}: {Count} selected from {CandidateCount} candidates.", normalizedMarket, selected.Count, candidates.Count);
        return new DynamicSymbolUniverseResult(normalizedMarket, candidates.Count, selected);
    }

    private async Task<IReadOnlyList<string>> ResolveCandidatesAsync(string market, CancellationToken cancellationToken)
    {
        if (market.Equals("BIST", StringComparison.OrdinalIgnoreCase) && _bistMarketGateway is not null)
        {
            return await _bistMarketGateway.GetUniverseAsync(cancellationToken);
        }

        return DefaultUniverse.TryGetValue(market, out var symbols)
            ? symbols
            : DefaultUniverse["Crypto"];
    }

    private static Dictionary<string, decimal> BuildExpectancyLookup(IEnumerable<Trade> trades)
    {
        return trades
            .Where(trade => trade.IsClosed)
            .GroupBy(trade => trade.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Average(trade => (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == Contracts.Enums.TradeSide.Sell ? -1 : 1)),
                StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record DynamicSymbolUniverseResult(
    string Market,
    int CandidateCount,
    IReadOnlyList<UniverseCandidate> SelectedSymbols);

public sealed record UniverseCandidate(
    string Symbol,
    double Score,
    decimal AverageVolume,
    decimal AverageRangePercent,
    decimal MomentumPercent,
    decimal RecentExpectancy,
    string Summary);
