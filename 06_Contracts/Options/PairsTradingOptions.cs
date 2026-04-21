namespace ThatsMyQuant.Contracts.Options;

public sealed class PairsTradingOptions
{
    public string SymbolA { get; set; } = "BTC";
    public string SymbolB { get; set; } = "ETH";
    public int WindowSize { get; set; } = 100;
    public decimal BaseAllocationUsd { get; set; } = 1_000m;
    public double EntryZScoreThreshold { get; set; } = 2.0;
    public int RebalanceInterval { get; set; } = 50;
    public int SignalCooldownTicks { get; set; } = 25;
}
