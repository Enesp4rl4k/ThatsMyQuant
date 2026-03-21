using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Trailing stop-loss service — dynamically moves the stop-loss in profit's direction
/// to lock gains while giving the trade room to breathe.
///
/// Logic:
///   - For BUY: trail = highestPrice - (ATR × multiplier)
///     * Only moves UP. Never moves down.
///   - For SELL: trail = lowestPrice + (ATR × multiplier)
///     * Only moves DOWN. Never moves up.
///   - Starts activating after price moves 1×ATR in profit direction (activation threshold).
/// </summary>
public static class TrailingStopService
{
    /// <summary>
    /// Update a position's trailing stop based on current price.
    /// Returns true if the trailing stop was hit.
    /// </summary>
    public static bool UpdateTrailingStop(PaperPosition position, decimal currentHigh, decimal currentLow, double atr, double multiplier = 2.0)
    {
        decimal atrDecimal = (decimal)(atr * multiplier);

        if (position.Side == "Buy")
        {
            // Update highest price
            if (currentHigh > position.HighestPriceSinceEntry)
                position.HighestPriceSinceEntry = currentHigh;

            // Activation: only start trailing after 1×ATR profit
            decimal activationPrice = position.EntryPrice + (decimal)atr;
            if (position.HighestPriceSinceEntry < activationPrice)
                return false;

            // Compute new trailing stop
            decimal newTrail = position.HighestPriceSinceEntry - atrDecimal;

            // Only move UP (lock profits, never give back)
            if (newTrail > position.TrailingStopPrice)
                position.TrailingStopPrice = newTrail;

            // Check if hit
            return currentLow <= position.TrailingStopPrice && position.TrailingStopPrice > 0;
        }
        else // Sell
        {
            if (currentLow < position.HighestPriceSinceEntry || position.HighestPriceSinceEntry == 0)
                position.HighestPriceSinceEntry = currentLow;

            decimal activationPrice = position.EntryPrice - (decimal)atr;
            if (position.HighestPriceSinceEntry > activationPrice && position.HighestPriceSinceEntry > 0)
                return false;

            decimal newTrail = position.HighestPriceSinceEntry + atrDecimal;

            if (newTrail < position.TrailingStopPrice || position.TrailingStopPrice == 0)
                position.TrailingStopPrice = newTrail;

            return currentHigh >= position.TrailingStopPrice && position.TrailingStopPrice > 0;
        }
    }
}
