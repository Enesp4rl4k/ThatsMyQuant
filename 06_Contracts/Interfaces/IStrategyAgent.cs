using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IStrategyAgent
{
    string StrategyTag { get; }
    int SwarmWeight { get; }
    bool CanHandle(Signal signal, MarketRegime regime);
    Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default);
}
