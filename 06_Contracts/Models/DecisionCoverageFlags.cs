namespace ThatsMyQuant.Contracts.Models;

[Flags]
public enum DecisionCoverageFlags
{
    None = 0,
    RegimeDetection = 1 << 0,
    TechnicalAnalysis = 1 << 1,
    AgentPipeline = 1 << 2,
    RiskGauntlet = 1 << 3,
    RiskRewardGate = 1 << 4,
    PositionSizing = 1 << 5,
    StrategyRouter = 1 << 6,
    EdgeScore = 1 << 7,
    Inference = 1 << 8
}
