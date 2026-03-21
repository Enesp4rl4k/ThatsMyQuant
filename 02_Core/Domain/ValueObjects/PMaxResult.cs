using ThatsMyQuant.Domain.Enums;

namespace ThatsMyQuant.Domain.ValueObjects;

/// <summary>
/// Result of the PMax indicator computation based on Kivanc Ozbilgic's TradingView script.
/// </summary>
public record PMaxResult
{
    public double MovingAverage { get; init; }
    public double Value { get; init; }
    public TradeSide? Signal { get; init; }
    public bool IsBullish { get; init; }
}
