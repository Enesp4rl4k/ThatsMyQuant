namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Backtest quality contract used for deterministic pass/fail gating.
/// Section: Backtest:QualityGate
/// </summary>
public sealed class BacktestQualityGateOptions
{
    public int ShortHorizonDays { get; set; } = 90;
    public int LongHorizonDays { get; set; } = 365;

    public int MinTrades { get; set; } = 80;
    public double MaxPbo { get; set; } = 0.35;
    public decimal MaxDrawdownPercent { get; set; } = 12m;
    public double MinProfitFactor { get; set; } = 1.25;
    public double MinWinRatePercent { get; set; } = 35.0;
    public decimal MinExpectancyPerTrade { get; set; } = 0.01m;
    public int MinWalkForwardWindows { get; set; } = 3;
    public double MinOosPositiveRatio { get; set; } = 0.55;

    public decimal ShortMinPnl { get; set; } = 0m;
    public double ShortMinSharpe { get; set; } = 1.10;
    public double ShortMinAverageRr { get; set; } = 1.10;

    public decimal LongMinPnl { get; set; } = 0m;
    public double LongMinSharpe { get; set; } = 1.10;
    public double LongMinAverageRr { get; set; } = 1.10;
}
