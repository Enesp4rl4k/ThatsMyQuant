using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Foundation;

public sealed class MicrostructureAgent : IFoundationAgent
{
    private readonly ILogger<MicrostructureAgent> _logger;

    public string AgentId => "Foundation.Microstructure";

    public MicrostructureAgent(ILogger<MicrostructureAgent> logger)
        => _logger = logger;

    public Task<FoundationSnapshot> ComputeAsync(
        IReadOnlyList<MarketTick> ticks,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (ticks.Count < 5)
            return Task.FromResult(FoundationSnapshot.Empty);

        var window = ticks.TakeLast(50).ToList();
        var buyTicks = window.Count(t => !t.IsBuyerMaker);
        var tickDirectionRatio = (double)buyTicks / window.Count;

        // Trade intensity: ticks per second
        var timeSpan = (window[^1].TimestampMs - window[0].TimestampMs) / 1000.0;
        var intensity = timeSpan > 0 ? window.Count / timeSpan : 0;

        return Task.FromResult(new FoundationSnapshot
        {
            TickDirectionRatio = tickDirectionRatio,
            BidAskImbalance = tickDirectionRatio - 0.5,
            SpreadBps = intensity // Hijacking SpreadBps for intensity or we could add it to model
        });
    }
}
