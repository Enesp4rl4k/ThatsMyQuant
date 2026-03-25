using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Aggregates telemetry from all 5 tiers of the trading engine for UI consumption.
/// </summary>
public record FiveTierSnapshot
{
    public FoundationSnapshot Foundation { get; init; } = FoundationSnapshot.Empty;
    public PatternSnapshot Patterns { get; init; } = PatternSnapshot.Empty;
    public IReadOnlyList<StrategyVote> Votes { get; init; } = Array.Empty<StrategyVote>();
    public RiskGuardResult RiskGauntlet { get; init; } = RiskGuardResult.Pass("Empty");
    public MetaAdaptation Meta { get; init; } = MetaAdaptation.Default;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
