namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Tracks a single symbol's open position state.
/// </summary>
public sealed record PositionState
{
    public string Symbol { get; init; } = "";
    public decimal EntryPrice { get; init; }
    public decimal Quantity { get; init; }
    public bool IsLong { get; init; }
    public DateTime OpenedAt { get; init; }

    // MAFE (Maximum Adverse / Favorable Excursion) Tracking
    public decimal HighestPrice { get; private set; }
    public decimal LowestPrice { get; private set; }
    public decimal MaxFavorableExcursionPercent { get; private set; }
    public decimal MaxAdverseExcursionPercent { get; private set; }

    /// <summary>
    /// Tracks the price movement to calculate real-time MFE/MAE (in percent).
    /// </summary>
    public void UpdatePrice(decimal currentPrice)
    {
        if (EntryPrice == 0) return;

        if (HighestPrice == 0 || currentPrice > HighestPrice) HighestPrice = currentPrice;
        if (LowestPrice == 0 || currentPrice < LowestPrice) LowestPrice = currentPrice;

        var currentChangePercent = ((currentPrice - EntryPrice) / EntryPrice) * 100m;
        var direction = IsLong ? 1m : -1m;
        var relativeChange = currentChangePercent * direction;

        if (relativeChange > MaxFavorableExcursionPercent) MaxFavorableExcursionPercent = relativeChange;
        if (relativeChange < MaxAdverseExcursionPercent) MaxAdverseExcursionPercent = relativeChange;
    }

    /// <summary>
    /// Calculates unrealized P&amp;L at a given market price.
    /// </summary>
    public decimal UnrealizedPnl(decimal currentPrice)
    {
        var direction = IsLong ? 1m : -1m;
        return (currentPrice - EntryPrice) * Quantity * direction;
    }
}
