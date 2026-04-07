namespace ThatsMyQuant.Contracts.Options;

public sealed class MarketRegimeDetectionOptions
{
    public double ShannonEntropyThreshold { get; set; } = 0.82;
    public double AtrPercentLowThreshold { get; set; } = 1.5;
    public double AtrPercentHighThreshold { get; set; } = 3.0;
    public double GarchLowThreshold { get; set; } = 1.0;
    public double GarchHighThreshold { get; set; } = 1.5;
    public double GarchOmega { get; set; } = 0.000005;
    public double GarchAlpha { get; set; } = 0.15;
    public double GarchBeta { get; set; } = 0.80;

    // Regime-specific technical parameters
    public int VolatileRsiPeriod { get; set; } = 14;
    public int VolatileEmaFast { get; set; } = 10;
    public int VolatileEmaSlow { get; set; } = 30;
    public double VolatileStopLossMultiplier { get; set; } = 3.0;
    public double VolatileTakeProfitMultiplier { get; set; } = 4.5;

    public int TrendingRsiPeriod { get; set; } = 10;
    public int TrendingEmaFast { get; set; } = 15;
    public int TrendingEmaSlow { get; set; } = 40;
    public double TrendingStopLossMultiplier { get; set; } = 2.0;
    public double TrendingTakeProfitMultiplier { get; set; } = 4.0;

    public int RangingRsiPeriod { get; set; } = 14;
    public int RangingEmaFast { get; set; } = 20;
    public int RangingEmaSlow { get; set; } = 50;
    public double RangingStopLossMultiplier { get; set; } = 1.5;
    public double RangingTakeProfitMultiplier { get; set; } = 2.0;
}
