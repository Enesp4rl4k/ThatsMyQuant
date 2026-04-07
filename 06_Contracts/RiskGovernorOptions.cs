namespace ThatsMyQuant.Contracts.Options;

public sealed class RiskGovernorOptions
{
    public decimal FractionalKelly { get; set; } = 0.50m;
    public decimal MinimumTradableFraction { get; set; } = 0.0025m;
    public decimal MaxPortfolioHeat { get; set; } = 0.30m;
    public decimal MaxSymbolExposure { get; set; } = 0.12m;
    public decimal MaxCorrelatedExposure { get; set; } = 0.20m;
    public int MaxOpenTrades { get; set; } = 5;
    public int MaxOpenTradesPerSymbol { get; set; } = 2;
    public decimal SoftDrawdownPercent { get; set; } = 4.0m;
    public decimal HardDrawdownPercent { get; set; } = 8.0m;
    public decimal MinimumDrawdownMultiplier { get; set; } = 0.25m;
    public int MinimumClosedTradesForAdaptiveSizing { get; set; } = 4;
    public int AdaptiveLookbackTrades { get; set; } = 12;
    public decimal MinimumPerformanceMultiplier { get; set; } = 0.55m;
    public decimal MaximumPerformanceMultiplier { get; set; } = 1.05m;
    public decimal PreferredEntrySlippagePercent { get; set; } = 0.08m;
    public decimal HardEntrySlippagePercent { get; set; } = 0.35m;
    public long PreferredExecutionLatencyMs { get; set; } = 200;
    public long HardExecutionLatencyMs { get; set; } = 1_000;
    public decimal MinimumExecutionHealthToTrade { get; set; } = 0.35m;
    public decimal MinimumExecutionMultiplier { get; set; } = 0.50m;
    public decimal MaximumExecutionMultiplier { get; set; } = 1.05m;
    public decimal SoftLiquidity24hVolume { get; set; } = 1_000_000m;
    public decimal HardLiquidity24hVolume { get; set; } = 250_000m;
    public int MaxTrackedTrades { get; set; } = 500;

    // Rate Limiter
    public int RateLimiterTokens { get; set; } = 10;
    public double RateLimiterRefillPerSecond { get; set; } = 10.0;

    // Loss Streak Guard
    public int LossStreakThreshold { get; set; } = 3;

    // Execution Health Weights
    public double SlippageScoreWeight { get; set; } = 0.45;
    public double LatencyScoreWeight { get; set; } = 0.35;
    public double VenueScoreWeight { get; set; } = 0.20;
}

public sealed class VpinOptions
{
    public decimal ToxicityThreshold { get; set; } = 0.70m;
    public decimal BucketVolumeSize { get; set; } = 1000m;
    public int BucketCount { get; set; } = 50;
}

public sealed class MonteCarloOptions
{
    public int DefaultIterations { get; set; } = 10_000;
    public int DefaultStepsPerIteration { get; set; } = 252;
    public double FifthPercentile { get; set; } = 0.05;
    public double NinetyNinthPercentile { get; set; } = 0.99;
}

public sealed class SwarmOptions
{
    public float MlDownsideThreshold { get; set; } = -0.01f;
    public float SuccessThreshold { get; set; } = 0.6f;
    public float ElevatedDriftPenalty { get; set; } = 0.15f;
    public decimal ElevatedDriftSizeMultiplier { get; set; } = 0.65m;
    public int MaxConsecutiveFailures { get; set; } = 3;
    public int HaltCooldownMinutes { get; set; } = 5;

    /// <summary>Per-agent timeout in seconds. If an agent does not complete within this window, a fallback result is used.</summary>
    public int AgentTimeoutSeconds { get; set; } = 5;
}

public sealed class ExecutionOptions
{
    public decimal HftMinVolume24h { get; set; } = 500_000m;
    public bool UseWorkerHandoff { get; set; }

    // === HftExecutionService ===
    public double LatencyGuardMultiplier { get; set; } = 1.35;
    public decimal SlippageGuardMultiplier { get; set; } = 1.75m;
    public int ConfidencePenaltyBaseline { get; set; } = 80;
    public double ConfidencePenaltyMultiplier { get; set; } = 0.8;
    public decimal LiquidityPenaltyMultiplier { get; set; } = 35m;

    // Volume-based slippage tiers
    public decimal VolumeHighRatioThreshold { get; set; } = 2.0m;
    public decimal VolumeHighSlippageMultiplier { get; set; } = 0.85m;
    public decimal VolumeMedRatioThreshold { get; set; } = 1.25m;
    public decimal VolumeMedSlippageMultiplier { get; set; } = 1.00m;
    public decimal VolumeLowRatioThreshold { get; set; } = 1.0m;
    public decimal VolumeLowSlippageMultiplier { get; set; } = 1.35m;
    public decimal VolumeFloorSlippageMultiplier { get; set; } = 1.80m;

    // === SlippageSimulator ===
    public decimal Tier1LiquidityFactor { get; set; } = 0.05m;
    public decimal AltcoinLiquidityFactor { get; set; } = 2.5m;
    public int CatastrophicSlippageCapBps { get; set; } = 5000;

    // === MinVolumeValidator ===
    public decimal MinVolumeThreshold { get; set; } = 1_000_000m;

    // === WebSocket ===
    public int ReceiveBufferSize { get; set; } = 65536;

    // === Quantity Validation ===
    /// <summary>Minimum notional value (price * qty) required for an order. Below this, the order is rejected.</summary>
    public decimal MinNotionalValue { get; set; } = 10m;
    /// <summary>Maximum allowed position size as a fraction of equity. Orders exceeding this are capped.</summary>
    public decimal MaxPositionSizePercent { get; set; } = 0.25m;
    /// <summary>Minimum quantity for any order. Orders below this are rejected.</summary>
    public decimal MinOrderQuantity { get; set; } = 0.00001m;
}

public sealed class RegimeOptions
{
    public decimal VolatileMaxPositionSizePercent { get; set; } = 2.5m;
    public decimal TrendingMaxPositionSizePercent { get; set; } = 7.5m;
    public int CandleLookbackCount { get; set; } = 100;
}

public sealed class FeatureOptions
{
    public int FeatureCount { get; set; } = 5;
    public long DriftMonitorWindowSize { get; set; } = 10_000;
}

public sealed class WebSocketOptions
{
    public string[] Symbols { get; set; } = ["btcusdt"];
    public string BaseUrl { get; set; } = "wss://stream.binance.com:9443/ws";
    public int ReconnectDelayMs { get; set; } = 3000;
    public int MaxReconnectAttempts { get; set; } = 50;
}
