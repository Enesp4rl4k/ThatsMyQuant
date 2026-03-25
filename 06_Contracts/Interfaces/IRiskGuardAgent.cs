using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IRiskGuardAgent
{
    string GuardId { get; }
    int Order { get; }
    Task<RiskGuardResult> EvaluateAsync(
        Signal signal,
        TradeDecision decision,
        IReadOnlyList<Trade> openTrades,
        CancellationToken ct = default);
}
