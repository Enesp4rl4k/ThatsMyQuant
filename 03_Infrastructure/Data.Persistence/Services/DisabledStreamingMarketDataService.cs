using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class DisabledStreamingMarketDataService : IStreamingMarketDataService
{
    // This disabled implementation never produces market events.
    public event Action<MarketTick>? OnTickReceived
    {
        add { }
        remove { }
    }

    public event Action<OrderBookSnapshot>? OnOrderBookUpdated
    {
        add { }
        remove { }
    }

    public Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            "HFT streaming is disabled. Configure a real streaming market data service before starting HFT workflows.");
    }

    public Task SubscribeAsync(string symbol) => Task.CompletedTask;
    public Task UnsubscribeAsync(string symbol) => Task.CompletedTask;

    public Task DisconnectAsync()
    {
        return Task.CompletedTask;
    }
}
