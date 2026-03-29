namespace ThatsMyQuant.Contracts.Options;

public sealed class TickReplayOptions
{
    public decimal TakerFee { get; set; } = 0.0005m;
    public decimal MakerFee { get; set; } = 0.0002m;
    public int LatencySimMs { get; set; } = 5;
    public double FillProbabilityThreshold { get; set; } = 0.6;
    public int CandleAggregationMs { get; set; } = 60_000;
    public decimal SpreadImpactFraction { get; set; } = 0.0003m;
}
