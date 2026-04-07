namespace ThatsMyQuant.Contracts.Options;

public sealed class ArbitrageOptions
{
    public long StaleQuoteThresholdMs { get; set; } = 100;
    public double MinEdgePercent { get; set; } = 0.10;
    public double RequiredProfitPercent { get; set; } = 0.05;
}
