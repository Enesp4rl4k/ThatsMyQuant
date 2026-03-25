using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IPatternAgent
{
    string AgentId { get; }
    Task<PatternSnapshot> AnalyzeAsync(
        Signal signal,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default);
}
