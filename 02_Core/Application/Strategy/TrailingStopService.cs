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
            if (position.OptimalPriceSinceEntry == 0 || currentLow < position.OptimalPriceSinceEntry)
                position.OptimalPriceSinceEntry = currentLow;

            decimal activationPrice = position.EntryPrice - (decimal)atr;
            if (position.OptimalPriceSinceEntry > activationPrice)
                return false;

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
            if (trade.OptimalPriceSinceEntry == 0 || currentLow < trade.OptimalPriceSinceEntry)
                trade.OptimalPriceSinceEntry = currentLow;

            decimal activationPrice = trade.EntryPrice - (decimal)atr;
            if (trade.OptimalPriceSinceEntry > activationPrice)
                return false;

            decimal newTrail = trade.OptimalPriceSinceEntry + atrDecimal;
            if (newTrail < trade.TrailingStopPrice || trade.TrailingStopPrice == 0)
                trade.TrailingStopPrice = newTrail;

            return currentHigh >= trade.TrailingStopPrice && trade.TrailingStopPrice > 0;
        }
    }
}
