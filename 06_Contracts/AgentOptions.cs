namespace ThatsMyQuant.Contracts.Options;

public sealed class MetaLearningAgentOptions
{
    // === OnlineLearningAdaptorAgent ===
    public float DecayAlpha { get; set; } = 0.05f;
    public float BaseWeight { get; set; } = 1.0f;
    public int MinHistoryForAdaptation { get; set; } = 5;

    // === ReinforcementLearningAgent ===
    public double ExplorationEpsilon { get; set; } = 0.10;
    public int WarmupTrades { get; set; } = 10;
}

public sealed class RiskGuardAgentOptions
{
    // === KellyPositionSizerAgent ===
    public double FractionalKelly { get; set; } = 0.25;
    public double MinEdge { get; set; } = 0.02;
    public double MaxPositionFraction { get; set; } = 0.20;
    public double TotalTransactionCostBps { get; set; } = 0.001;

    // === PortfolioOptimiserAgent ===
    public int MaxOpenTrades { get; set; } = 8;
    public decimal MaxPortfolioHeat { get; set; } = 0.15m;

    // === CorrelationGuardAgent ===
    public int MaxSameSymbolPositions { get; set; } = 1;
    public double HighCorrelationThreshold { get; set; } = 0.80;
}

public sealed class PatternAgentOptions
{
    // === BollingerBandAgent ===
    public int BollingerPeriod { get; set; } = 20;
    public double BollingerStdDevMultiplier { get; set; } = 2.0;
    public double BollingerSqueezeThreshold { get; set; } = 0.02;

    // === IchimokuAgent ===
    public int IchimokuTenkanPeriod { get; set; } = 9;
    public int IchimokuKijunPeriod { get; set; } = 26;
    public int IchimokuSenkouSpanBPeriod { get; set; } = 52;
}
