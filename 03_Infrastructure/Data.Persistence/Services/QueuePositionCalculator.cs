using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

public static class QueuePositionCalculator
{
    public static decimal CalculateVolumeAhead(OrderBookSnapshot book, decimal price, TradeSide side)
    {
        decimal volumeAhead = 0;
        var levels = side == TradeSide.Buy ? book.Bids : book.Asks;

        foreach (var level in levels)
        {
            if (side == TradeSide.Buy)
            {
                if (level.Price > price) volumeAhead += level.Quantity;
                else if (level.Price == price) 
                {
                    volumeAhead += level.Quantity * 0.5m;
                    break;
                }
            }
            else // Sell
            {
                if (level.Price < price) volumeAhead += level.Quantity;
                else if (level.Price == price)
                {
                    volumeAhead += level.Quantity * 0.5m;
                    break;
                }
            }
        }

        return volumeAhead;
    }
}
