namespace ThatsMyQuant.Contracts.Models;

public record LimitOrderExecutionConfig
{
    public bool PostOnly { get; init; } = true;
    public decimal MaxSpreadTolerancePercent { get; init; } = 0.15m;
    public decimal MinQueuePositionPercentile { get; init; } = 0.30m;
    public long MaxOrderAgeMs { get; init; } = 30_000;
    public int MaxRetries { get; init; } = 3;
    public decimal PriceImprovementTicks { get; init; } = 0.0001m;
    public decimal UrgencyThreshold { get; init; } = 0.85m;
    public decimal FadeRatePerRetry { get; init; } = 0.0002m;
    public bool AllowMarketFallback { get; init; } = true;
}
