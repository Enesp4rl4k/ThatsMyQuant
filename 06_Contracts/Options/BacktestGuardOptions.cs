namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Entry-quality guards for backtest engine.
/// Section: Backtest:Guards
/// </summary>
public sealed class BacktestGuardOptions
{
    public bool EnableRegimeTimeframeVeto { get; set; } = true;
    public bool BlockVolatileOnOneHour { get; set; } = true;
    public bool BlockVolatileOnFourHour { get; set; } = false;
    public bool BlockRangingOnOneMinute { get; set; } = true;
    public bool EnablePullbackFilter { get; set; } = true;
    public decimal PullbackAtrMultiplier { get; set; } = 0.80m;
    public bool EnableHigherTimeframeBiasGate { get; set; } = true;
    public bool EnableCooldownGate { get; set; } = false;

    // Keep expectancy telemetry but don't throttle entries by default in backtests.
    // It was masking strategy quality by starving valid samples.
    public bool EnableExpectancyGuard { get; set; } = false;
    public int ExpectancyWindowTrades { get; set; } = 24;
    public decimal MinExpectancyPerTrade { get; set; } = -5m;
    public int ExpectancyCooldownBars { get; set; } = 12;

    public BacktestGuardRiskProfile LowRisk { get; set; } = new()
    {
        ConfidencePenaltyOnVetoRegime = 10,
        PositionSizeMultiplierOnVetoRegime = 0.55m,
        ExpectancyWindowTrades = 30,
        MinExpectancyPerTrade = 0m,
        ExpectancyCooldownBars = 24
    };

    public BacktestGuardRiskProfile MediumRisk { get; set; } = new()
    {
        ConfidencePenaltyOnVetoRegime = 6,
        PositionSizeMultiplierOnVetoRegime = 0.75m,
        ExpectancyWindowTrades = 24,
        MinExpectancyPerTrade = -5m,
        ExpectancyCooldownBars = 12
    };

    public BacktestGuardRiskProfile AggressiveRisk { get; set; } = new()
    {
        ConfidencePenaltyOnVetoRegime = 3,
        PositionSizeMultiplierOnVetoRegime = 0.90m,
        ExpectancyWindowTrades = 16,
        MinExpectancyPerTrade = -12m,
        ExpectancyCooldownBars = 6
    };
}

public sealed class BacktestGuardRiskProfile
{
    public int ConfidencePenaltyOnVetoRegime { get; set; }
    public decimal PositionSizeMultiplierOnVetoRegime { get; set; } = 1m;
    public int ExpectancyWindowTrades { get; set; } = 24;
    public decimal MinExpectancyPerTrade { get; set; } = -5m;
    public int ExpectancyCooldownBars { get; set; } = 12;
}
