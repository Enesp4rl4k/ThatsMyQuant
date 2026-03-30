using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

public static class TrailingStopService
{
    public static bool UpdateTrailingStop(PaperPosition position, decimal currentHigh, decimal currentLow, double atr, double multiplier = 2.0)
    {
        decimal atrDecimal = (decimal)(atr * multiplier);

        if (position.Side == TradeSide.Buy)
        {
            if (currentHigh > position.OptimalPriceSinceEntry)
                position.OptimalPriceSinceEntry = currentHigh;

            decimal activationPrice = position.EntryPrice + (decimal)atr;
            if (position.OptimalPriceSinceEntry < activationPrice)
                return false;

            decimal newTrail = position.OptimalPriceSinceEntry - atrDecimal;
            if (newTrail > position.TrailingStopPrice)
                position.TrailingStopPrice = newTrail;

            return currentLow <= position.TrailingStopPrice && position.TrailingStopPrice > 0;
        }
        else // Sell
        {
            if (currentLow < position.OptimalPriceSinceEntry || position.OptimalPriceSinceEntry == 0)
                position.OptimalPriceSinceEntry = currentLow;

            decimal activationPrice = position.EntryPrice - (decimal)atr;
            if (position.OptimalPriceSinceEntry > activationPrice && (position.OptimalPriceSinceEntry > 0 || position.OptimalPriceSinceEntry == 0))
            {
                // If it's the first check, we need to handle the 0 initialization
                if (position.OptimalPriceSinceEntry == 0) position.OptimalPriceSinceEntry = currentLow;
                if (position.OptimalPriceSinceEntry > activationPrice) return false;
            }

            decimal newTrail = position.OptimalPriceSinceEntry + atrDecimal;
            if (newTrail < position.TrailingStopPrice || position.TrailingStopPrice == 0)
                position.TrailingStopPrice = newTrail;

            return currentHigh >= position.TrailingStopPrice && position.TrailingStopPrice > 0;
        }
    }

    public static bool UpdateTrailingStop(Trade trade, decimal currentHigh, decimal currentLow, double atr, double multiplier = 2.0)
    {
        decimal atrDecimal = (decimal)(atr * multiplier);

        if (trade.Side == TradeSide.Buy)
        {
            if (currentHigh > trade.OptimalPriceSinceEntry)
                trade.OptimalPriceSinceEntry = currentHigh;

            decimal activationPrice = trade.EntryPrice + (decimal)atr;
            if (trade.OptimalPriceSinceEntry < activationPrice)
                return false;

            decimal newTrail = trade.OptimalPriceSinceEntry - atrDecimal;
            if (newTrail > trade.TrailingStopPrice)
                trade.TrailingStopPrice = newTrail;

            return currentLow <= trade.TrailingStopPrice && trade.TrailingStopPrice > 0;
        }
        else
        {
            if (currentLow < trade.OptimalPriceSinceEntry || trade.OptimalPriceSinceEntry == 0)
                trade.OptimalPriceSinceEntry = currentLow;

            decimal activationPrice = trade.EntryPrice - (decimal)atr;
            if (trade.OptimalPriceSinceEntry > activationPrice && (trade.OptimalPriceSinceEntry > 0 || trade.OptimalPriceSinceEntry == 0))
            {
                if (trade.OptimalPriceSinceEntry == 0) trade.OptimalPriceSinceEntry = currentLow;
                if (trade.OptimalPriceSinceEntry > activationPrice) return false;
            }

            decimal newTrail = trade.OptimalPriceSinceEntry + atrDecimal;
            if (newTrail < trade.TrailingStopPrice || trade.TrailingStopPrice == 0)
                trade.TrailingStopPrice = newTrail;

            return currentHigh >= trade.TrailingStopPrice && trade.TrailingStopPrice > 0;
        }
    }
}
