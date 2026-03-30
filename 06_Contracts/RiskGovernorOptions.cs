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
}
