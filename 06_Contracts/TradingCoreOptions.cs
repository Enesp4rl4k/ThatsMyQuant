namespace ThatsMyQuant.Contracts.Options;

public sealed class TradingCoreOptions
{
    // === AdaptiveSizingService ===
    public int ConfidenceHighThreshold { get; set; } = 85;
    public int ConfidenceMediumThreshold { get; set; } = 75;
    public int ConfidenceLowThreshold { get; set; } = 65;
    public decimal ConfidenceHighMultiplier { get; set; } = 1.00m;
    public decimal ConfidenceMediumMultiplier { get; set; } = 0.85m;
    public decimal ConfidenceLowMultiplier { get; set; } = 0.65m;
    public decimal ConfidenceFloorMultiplier { get; set; } = 0.45m;
    public decimal SessionLondonMultiplier { get; set; } = 1.00m;
    public decimal SessionNyMultiplier { get; set; } = 0.95m;
    public decimal SessionAsiaMultiplier { get; set; } = 0.70m;
    public decimal SessionDeadZoneMultiplier { get; set; } = 0.55m;
    public decimal SessionBistMultiplier { get; set; } = 0.90m;

    // === EdgeScoreService ===
    public decimal WeightSignal { get; set; } = 0.30m;
    public decimal WeightHistorical { get; set; } = 0.30m;
    public decimal WeightExecution { get; set; } = 0.25m;
    public decimal WeightRisk { get; set; } = 0.15m;
    public decimal CoreWeight { get; set; } = 0.70m;
    public decimal ExecutionBlendWeight { get; set; } = 0.30m;
    public decimal ExecutionGateThreshold { get; set; } = 60m;
    public decimal RiskGateThreshold { get; set; } = 50m;
    public decimal BandFullThreshold { get; set; } = 85m;
    public decimal BandReducedThreshold { get; set; } = 70m;
    public decimal BandProbeThreshold { get; set; } = 55m;

    // === CommissionGuard ===
    public decimal DefaultTakerFeeRate { get; set; } = 0.0004m;

    // === CandlestickPatternDetector ===
    public decimal DojiThreshold { get; set; } = 0.1m;
    public decimal IndecisionThreshold { get; set; } = 0.3m;
    public decimal StrongBodyThreshold { get; set; } = 0.5m;

    // === PairsTradingEngine ===
    public double ZScoreThresholdEntry { get; set; } = 2.0;
    public double ZScoreThresholdExit { get; set; } = 0.5;

    // === ExecutionImpactModel ===
    public decimal BaselineDailyVolume { get; set; } = 100_000m;
    public decimal BaselineDailyVolatility { get; set; } = 0.02m;
    public decimal ImpactCoefficient { get; set; } = 0.15m;

    // === PositionSupervisorService ===
    /// <summary>Breakeven buffer as a fraction of entry price. E.g. 0.0005 = 0.05% above/below entry.</summary>
    public decimal BreakevenBufferFraction { get; set; } = 0.0005m;
}
