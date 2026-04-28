using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Strategy/analysis coverage contract required for backtest parity.
/// Section: Backtest:Parity
/// </summary>
public sealed class BacktestParityOptions
{
    public bool RequireParityCoverage { get; set; } = false;
    /// <summary>
    /// When true, the quality gate requires the ML model file to exist and load.
    /// Default: false — ML inference is advisory. The system gracefully degrades
    /// without a trained model. Set to true only once a validated ONNX model ships.
    /// </summary>
    public bool RequireMlModelAvailability { get; set; } = false;
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
