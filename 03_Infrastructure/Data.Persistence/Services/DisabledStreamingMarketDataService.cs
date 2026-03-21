using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class DisabledStreamingMarketDataService : IStreamingMarketDataService
{
    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            "HFT streaming is disabled. Configure a real streaming market data service before starting HFT workflows.");
    }

    public Task DisconnectAsync()
    {
        return Task.CompletedTask;
    }
}
