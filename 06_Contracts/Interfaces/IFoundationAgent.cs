using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IFoundationAgent
{
    string AgentId { get; }
    Task<FoundationSnapshot> ComputeAsync(
        IReadOnlyList<MarketTick> ticks,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default);
}
