using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

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

        // Convert to Span for high-performance iteration gracefully
        ReadOnlySpan<Candle> span;
        if (candles is List<Candle> candlesList)
            span = CollectionsMarshal.AsSpan(candlesList);
        else if (candles is Candle[] candlesArray)
            span = candlesArray;
        else
            span = candles.ToArray();
        var lastN = span.Length > 100 ? span.Slice(span.Length - 100) : span;

        var volumeAtPrice = BuildVolumeAtPrice(lastN);
        var vwap = ComputeVwap(lastN);
        var stdDev = ComputeVwapStdDev(lastN, vwap);

        return Task.FromResult(new FoundationSnapshot
        {
            Vwap = vwap,
            VwapStdDev = stdDev,
            VolumeAtPrice = volumeAtPrice,
            WallLevel = FindPointOfControl(volumeAtPrice),
            TickDirectionRatio = 0.5
        });
    }

    private static IReadOnlyDictionary<decimal, decimal> BuildVolumeAtPrice(ReadOnlySpan<Candle> candles)
    {
        var map = new Dictionary<decimal, decimal>(128); // Pre-size to avoid resizes
        foreach (var c in candles)
        {
            var mid = Math.Round(c.Close, 2);
            map[mid] = map.TryGetValue(mid, out var v) ? v + c.Volume : c.Volume;
        }
        return map;
    }

    private static decimal ComputeVwap(ReadOnlySpan<Candle> candles)
    {
        decimal cumTpv = 0m, cumVol = 0m;
        foreach (var c in candles)
        {
            var tp = (c.High + c.Low + c.Close) / 3m;
            cumTpv += tp * c.Volume;
            cumVol += c.Volume;
        }
        return cumVol > 0 ? cumTpv / cumVol : 0m;
    }

    private static decimal ComputeVwapStdDev(ReadOnlySpan<Candle> candles, decimal vwap)
    {
        if (candles.Length < 2) return 0m;
        decimal cumVol = 0m, cumSq = 0m;
        foreach (var c in candles)
        {
            var tp = (c.High + c.Low + c.Close) / 3m;
            var diff = tp - vwap;
            cumSq += diff * diff * c.Volume;
            cumVol += c.Volume;
        }
        return cumVol > 0 ? (decimal)Math.Sqrt((double)(cumSq / cumVol)) : 0m;
    }

    private static decimal FindPointOfControl(IReadOnlyDictionary<decimal, decimal> volumeAtPrice)
    {
        if (volumeAtPrice.Count == 0) return 0m;
        decimal maxVol = -1;
        decimal poc = 0;
        foreach (var kvp in volumeAtPrice)
        {
            if (kvp.Value > maxVol)
            {
                maxVol = kvp.Value;
                poc = kvp.Key;
            }
        }
        return poc;
    }
}

