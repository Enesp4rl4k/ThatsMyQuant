using System.Collections.Concurrent;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// Avoids the expensive 'new Signal()' allocation during the 1,000 tick/sec market data ingest loop.
/// Rents pre-instantiated generic objects from memory to construct execution directives, wiping them clean upon return.
/// </summary>
public class AllocationFreeSignalPool
{
    private readonly ConcurrentStack<Signal> _pool = new();

    public AllocationFreeSignalPool(int preWarmCount = 5000)
    {
        for (int i = 0; i < preWarmCount; i++)
        {
            _pool.Push(new Signal());
        }
    }

    /// <summary>
    /// Rises zero memory pressure by pulling an existing unlinked signal object.
    /// </summary>
    public Signal Rent(string symbol, decimal price)
    {
        if (_pool.TryPop(out var signal))
        {
            signal.Symbol = symbol;
            signal.Price = price;
            return signal;
        }

        return new Signal { Symbol = symbol, Price = price };
    }

    /// <summary>
    /// Cleans ALL fields and restores the object to the Stack pool.
    /// Every mutable field must be reset to prevent stale data leaking into the next rental.
    /// </summary>
    public void Return(Signal signal)
    {
        signal.Id = Guid.NewGuid();
        signal.Source = string.Empty;
        signal.Symbol = string.Empty;
        signal.Side = TradeSide.Buy;
        signal.Price = 0;
        signal.Status = TradeStatus.Pending;
        signal.RetryCount = 0;
        signal.Quantity = 0m;
        signal.Confidence = 0;
        signal.Reasoning = string.Empty;
        signal.StrategyTag = "TrendConfluence";
        signal.RegimeTag = "Unknown";
        signal.MarketType = MarketType.BIST;
        signal.Exchange = "Binance";
        signal.Timestamp = DateTime.UtcNow;
        signal.Volume24h = 0;
        signal.OrderFlowImbalance = null;
        signal.BestBid = null;
        signal.BestAsk = null;
        signal.WallAgeMs = null;
        signal.ExecutionAction = ExecutionActionType.None;
        signal.PriceActionSignal = PriceActionType.None;
        signal.ReasonFlags = SignalReasonFlags.None;
        signal.Metadata.Clear();

        _pool.Push(signal);
    }
}
