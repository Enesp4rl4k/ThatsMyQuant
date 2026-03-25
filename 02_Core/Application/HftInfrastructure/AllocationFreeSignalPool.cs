using System.Collections.Concurrent;
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
    /// Cleans and restores the object to the Stack pool.
    /// </summary>
    public void Return(Signal signal)
    {
        signal.Symbol = string.Empty;
        signal.Price = 0;
        _pool.Push(signal);
    }
}
