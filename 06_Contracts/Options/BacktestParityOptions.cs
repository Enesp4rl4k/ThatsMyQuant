using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Strategy/analysis coverage contract required for backtest parity.
/// Section: Backtest:Parity
/// </summary>
public sealed class BacktestParityOptions
{
    public bool RequireParityCoverage { get; set; } = false;
    public bool RequireMlModelAvailability { get; set; } = true;
    public bool RequireInferencePredictionValidity { get; set; } = false;

    public DecisionCoverageFlags RequiredCoverageFlags { get; set; } =
        DecisionCoverageFlags.RegimeDetection
        | DecisionCoverageFlags.TechnicalAnalysis
        | DecisionCoverageFlags.AgentPipeline
        | DecisionCoverageFlags.RiskGauntlet
        | DecisionCoverageFlags.RiskRewardGate
        | DecisionCoverageFlags.PositionSizing
        | DecisionCoverageFlags.StrategyRouter
        | DecisionCoverageFlags.EdgeScore
        | DecisionCoverageFlags.Inference;
}
