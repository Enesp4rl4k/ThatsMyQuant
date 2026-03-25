using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Interface for real-time WebSocket HFT streaming.
/// Replaces the REST API IMarketDataService for execution engines.
/// </summary>
public interface IStreamingMarketDataService
{
    event Action<MarketTick>? OnTickReceived;
    event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    Task ConnectAsync(string[] symbols, CancellationToken cancellationToken);
    Task DisconnectAsync();
}
