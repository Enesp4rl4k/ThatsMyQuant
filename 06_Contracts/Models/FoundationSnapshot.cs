namespace ThatsMyQuant.Contracts.Models;

public record FoundationSnapshot
{
    public double BidAskImbalance { get; init; }
    public decimal Vwap { get; init; }
    public decimal VwapStdDev { get; init; }
    public double TickDirectionRatio { get; init; }
    public double SpreadBps { get; init; }
    public decimal WallLevel { get; init; }
    public IReadOnlyDictionary<decimal, decimal> VolumeAtPrice { get; init; }
        = new Dictionary<decimal, decimal>();

    public static readonly FoundationSnapshot Empty = new();
}
