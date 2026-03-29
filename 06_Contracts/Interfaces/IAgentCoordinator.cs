using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Abstraction for the 5-Tier Agent Hierarchy Orchestrator.
/// Enables testability by allowing mock/substitute implementations.
/// </summary>
public interface IAgentCoordinator
{
    Task ResetAllAgentsAsync(string symbol, CancellationToken ct = default);

    Task<FoundationSnapshot> BuildFoundationAsync(
        IReadOnlyList<MarketTick> ticks,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default);

    Task<PatternSnapshot> BuildPatternAsync(
        Signal signal,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default);

    Task<IReadOnlyList<StrategyVote>> CollectStrategyVotesAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        MarketRegime regime,
        CancellationToken ct = default);

    Task<FiveTierSnapshot> RunFullPipelineAsync(
        Signal signal,
        IReadOnlyList<MarketTick> ticks,
        IReadOnlyList<Candle> candles,
        MarketRegime regime,
        IReadOnlyList<Trade> openTrades,
        IReadOnlyList<Trade> recentHistory,
        CancellationToken ct = default);

    Task<RiskGuardResult> RunRiskGauntletAsync(
        Signal signal,
        TradeDecision decision,
        IReadOnlyList<Trade> openTrades,
        CancellationToken ct = default);

    Task<MetaAdaptation> RunMetaAdaptationAsync(
        IReadOnlyList<Trade> recentHistory,
        MarketRegime regime,
        CancellationToken ct = default);
}
