using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Foundation;

public sealed class OrderBookDepthAnalyzer : IFoundationAgent
{
    private readonly ILogger<OrderBookDepthAnalyzer> _logger;
    private const int DepthLevels = 10;

    public string AgentId => "Foundation.OrderBookDepth";

    public OrderBookDepthAnalyzer(ILogger<OrderBookDepthAnalyzer> logger)
        => _logger = logger;

    public Task<FoundationSnapshot> ComputeAsync(
        IReadOnlyList<MarketTick> ticks,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (ticks.Count == 0)
            return Task.FromResult(FoundationSnapshot.Empty);

        var latest = ticks[^1];
        var window = ticks.TakeLast(100).ToList();
        
        double bidVol = window.Where(t => !t.IsBuyerMaker).Sum(t => (double)t.Volume);
        double askVol = window.Where(t => t.IsBuyerMaker).Sum(t => (double)t.Volume);
        
        var totalVol = bidVol + askVol;
        var imbalance = totalVol > 0 ? (bidVol - askVol) / totalVol : 0.0;

        decimal midPrice = candles.Count > 0 ? candles[^1].Close : latest.Price;
        
        var snapshot = new FoundationSnapshot
        {
            BidAskImbalance = imbalance,
            Vwap = midPrice,
            SpreadBps = midPrice > 0 ? (double)((latest.Price - midPrice) / midPrice * 10_000m) : 0.0,
            WallLevel = DetectWall(window),
            VwapStdDev = 0m,
            TickDirectionRatio = window.Count > 0 ? (double)window.Count(t => !t.IsBuyerMaker) / window.Count : 0.5
        };

        return Task.FromResult(snapshot);
    }

    private static decimal DetectWall(IReadOnlyList<MarketTick> ticks)
    {
        if (ticks.Count < 10) return 0m;

        var priceGroups = ticks
            .GroupBy(t => Math.Round(t.Price, 4))
            .Select(g => new { Price = g.Key, TotalVol = g.Sum(t => t.Volume) })
            .ToList();

        var avgVol = priceGroups.Average(p => p.TotalVol);
        var wall = priceGroups.OrderByDescending(p => p.TotalVol).FirstOrDefault();

        // Wall detection: level with > 3x average volume
        if (wall != null && wall.TotalVol > avgVol * 3)
            return wall.Price;

        return 0m;
    }
}
