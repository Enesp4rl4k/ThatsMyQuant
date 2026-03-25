namespace ThatsMyQuant.Contracts.Enums;

/// <summary>
/// Strictly-typed mapping for Price Action signatures.
/// Replaces the Garbage Collector string allocation mapping.
/// </summary>
public enum PriceActionType
{
    None = 0,
    ExhaustionHigh = 1,
    ExhaustionLow = 2
}
