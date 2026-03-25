using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Foundation;

public sealed class VolumeProfileAgent : IFoundationAgent
{
    private readonly ILogger<VolumeProfileAgent> _logger;

    public string AgentId => "Foundation.VolumeProfile";

    public VolumeProfileAgent(ILogger<VolumeProfileAgent> logger)
        => _logger = logger;

    public Task<FoundationSnapshot> ComputeAsync(
        IReadOnlyList<MarketTick> ticks,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count == 0)
            return Task.FromResult(FoundationSnapshot.Empty);

        var volumeAtPrice = BuildVolumeAtPrice(candles);
        var vwap = ComputeVwap(candles);
        var stdDev = ComputeVwapStdDev(candles, vwap);

        return Task.FromResult(new FoundationSnapshot
        {
            Vwap = vwap,
            VwapStdDev = stdDev,
            VolumeAtPrice = volumeAtPrice,
            WallLevel = FindPointOfControl(volumeAtPrice),
            TickDirectionRatio = 0.5
        });
    }

    private static IReadOnlyDictionary<decimal, decimal> BuildVolumeAtPrice(IReadOnlyList<Candle> candles)
    {
        var map = new Dictionary<decimal, decimal>();
        foreach (var c in candles.TakeLast(100))
        {
            var mid = Math.Round(c.Close, 2);
            map[mid] = map.TryGetValue(mid, out var v) ? v + c.Volume : c.Volume;
        }
        return map;
    }

    private static decimal ComputeVwap(IReadOnlyList<Candle> candles)
    {
        decimal cumTpv = 0m, cumVol = 0m;
        foreach (var c in candles.TakeLast(100))
        {
            var tp = (c.High + c.Low + c.Close) / 3m;
            cumTpv += tp * c.Volume;
            cumVol += c.Volume;
        }
        return cumVol > 0 ? cumTpv / cumVol : 0m;
    }

    private static decimal ComputeVwapStdDev(IReadOnlyList<Candle> candles, decimal vwap)
    {
        if (candles.Count < 2) return 0m;
        decimal cumVol = 0m, cumSq = 0m;
        foreach (var c in candles.TakeLast(100))
        {
            var tp = (c.High + c.Low + c.Close) / 3m;
            var diff = tp - vwap;
            cumSq += diff * diff * c.Volume;
            cumVol += c.Volume;
        }
        return cumVol > 0 ? (decimal)Math.Sqrt((double)(cumSq / cumVol)) : 0m;
    }

    private static decimal FindPointOfControl(IReadOnlyDictionary<decimal, decimal> volumeAtPrice)
        => volumeAtPrice.Count == 0 ? 0m
            : volumeAtPrice.MaxBy(kvp => kvp.Value).Key;
}
