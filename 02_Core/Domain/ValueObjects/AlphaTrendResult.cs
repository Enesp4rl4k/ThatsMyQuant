using ThatsMyQuant.Domain.Enums;

namespace ThatsMyQuant.Domain.ValueObjects;

/// <summary>
/// Result of the AlphaTrend indicator computation.
/// AlphaTrend is an ATR-based adaptive trend indicator by KivancOzbilgic.
/// </summary>
public record AlphaTrendResult
{
    /// <summary>Current AlphaTrend value (AT[0]).</summary>
    public double Value { get; init; }

    /// <summary>AlphaTrend value 2 bars back (AT[2]) — used for crossover signals.</summary>
    public double PreviousValue { get; init; }

    /// <summary>Buy/Sell/None signal based on AT crossover with AT[2].</summary>
    public TradeSide? Signal { get; init; }

    /// <summary>True if the signal is confirmed via barsSince filter (no duplicate signals).</summary>
    public bool IsConfirmed { get; init; }

    /// <summary>Bullish = AT > AT[2], Bearish = AT &lt; AT[2].</summary>
    public bool IsBullish { get; init; }
}
