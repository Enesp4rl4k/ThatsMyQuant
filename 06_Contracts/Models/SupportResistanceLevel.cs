namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// A detected support or resistance level from the Absorbing System indicator.
/// Based on pivot points + wick absorption analysis.
/// </summary>
public record SupportResistanceLevel
{
    /// <summary>Price level of the S/R zone.</summary>
    public decimal Price { get; init; }

    /// <summary>Support or Resistance.</summary>
    public SRType Type { get; init; }

    /// <summary>Bar index where this level was detected.</summary>
    public int BarIndex { get; init; }

    /// <summary>True if price has broken through this level.</summary>
    public bool IsBroken { get; init; }

    /// <summary>How many times this level has been tested without breaking.</summary>
    public int TestCount { get; init; }

    /// <summary>Timestamp of detection.</summary>
    public DateTime DetectedAt { get; init; }
}

public enum SRType
{
    Support,
    Resistance
}
