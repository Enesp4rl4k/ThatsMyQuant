using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Patterns;

public sealed class FibonacciAgent : IPatternAgent
{
    private readonly ILogger<FibonacciAgent> _logger;
    private static readonly double[] FibLevels = [0.236, 0.382, 0.500, 0.618, 0.786, 1.000];

    public string AgentId => "Pattern.Fibonacci";

    public FibonacciAgent(ILogger<FibonacciAgent> logger)
        => _logger = logger;

    public Task<PatternSnapshot> AnalyzeAsync(
        Signal signal,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < 20)
            return Task.FromResult(PatternSnapshot.Empty);

        var swing = candles.TakeLast(50).ToList();
        var high = swing.Max(c => c.High);
        var low = swing.Min(c => c.Low);
        var range = high - low;

        if (range == 0m)
            return Task.FromResult(PatternSnapshot.Empty);

        var retracementDepth = (double)((signal.Price - low) / range);

        var nearestFib = FibLevels.MinBy(f => Math.Abs(f - retracementDepth));
        var fibPrice = low + range * (decimal)nearestFib;
        var proximityPct = Math.Abs((double)((signal.Price - fibPrice) / fibPrice));

        var tags = new List<string>();
        if (proximityPct < 0.005)
            tags.Add($"FibProximity_{nearestFib:P0}");

        return Task.FromResult(new PatternSnapshot
        {
            FibNearestLevel = fibPrice,
            FibRetracementDepth = retracementDepth,
            PatternTags = tags
        });
    }
}
