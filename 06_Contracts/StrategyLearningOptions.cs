namespace ThatsMyQuant.Contracts.Options;

public sealed class StrategyLearningOptions
{
    // === Shared ===
    public int TradeLookback { get; set; } = 30;
    public int MinSample { get; set; } = 4;

    // === ExitPolicyLearningService (live sizing) ===
    public decimal LiveSizingMinSample { get; set; } = 3m;
    public double ExceptionalProfitFactor { get; set; } = 1.40;
    public double HealthyProfitFactor { get; set; } = 1.20;
    public double UnderperformingProfitFactor { get; set; } = 0.95;
    public double SoftProfitFactor { get; set; } = 1.05;
    public double ExceptionalWinRate { get; set; } = 58;
    public double HealthyWinRate { get; set; } = 50;
    public double UnderperformingWinRate { get; set; } = 40;
    public double SoftWinRate { get; set; } = 45;
    public decimal ExceptionalMultiplier { get; set; } = 1.15m;
    public decimal HealthyMultiplier { get; set; } = 1.08m;
    public decimal UnderperformingMultiplier { get; set; } = 0.80m;
    public decimal SoftMultiplier { get; set; } = 0.90m;
    public double SevereDriftThreshold { get; set; } = 0.65;
    public decimal SevereDriftMultiplier { get; set; } = 0.78m;
    public double ElevatedDriftThreshold { get; set; } = 0.45;
    public decimal ElevatedDriftMultiplier { get; set; } = 0.88m;

    // === ExitPolicyLearningService (policy qualification) ===
    public decimal PolicyMinWeightedTrades { get; set; } = 2.5m;
    public double PolicyMinProfitFactor { get; set; } = 1.05;
    public double PolicyMinWinRate { get; set; } = 42;
    public decimal PolicyScoreCompareThreshold { get; set; } = 1.05m;

    // === StrategyExpectancyService ===
    public int SessionMinTrades { get; set; } = 3;
    public double BlockProfitFactorThreshold { get; set; } = 0.95;
    public double BlockSessionProfitFactorThreshold { get; set; } = 1.00;
    public double ReduceProfitFactorThreshold { get; set; } = 1.10;
    public double ReduceWinRateThreshold { get; set; } = 0.45;
    public decimal ReduceSizeMultiplier { get; set; } = 0.60m;
    public decimal SessionSoftMultiplier { get; set; } = 0.75m;
    public decimal ComputePnlFeeRate { get; set; } = 0.0005m;
}
