using Microsoft.AspNetCore.SignalR;
using ThatsMyQuant.Contracts.Models;
using Listener.Api.Services;

namespace Listener.Api.Hubs;

public class MarketDataHub : Hub
{
    private readonly MarketDataBroadcastService _broadcaster;
    private readonly OkxOrderBookSubscriber?   _obSubscriber;

    public MarketDataHub(MarketDataBroadcastService broadcaster, IServiceProvider sp)
    {
        _broadcaster  = broadcaster;
        _obSubscriber = sp.GetService<OkxOrderBookSubscriber>();
    }

    // ── Price-tick subscriptions ──────────────────────────────────────────────

    public async Task SubscribeToMarket(string symbol)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, symbol);
        await _broadcaster.SubscribeSymbolAsync(symbol);
    }

    public async Task UnsubscribeFromMarket(string symbol)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, symbol);
        await _broadcaster.UnsubscribeSymbolAsync(symbol);
    }

    // ── L2 Order Book subscriptions ──────────────────────────────────────────
    // Group name: "ob:{symbol}" — isolated from price-tick groups.

    /// <summary>
    /// Join the L2 order book stream for <paramref name="symbol"/>.
    /// The server will push <c>OrderBookSnapshot</c> messages via SignalR as
    /// OKX delivers books5 updates (~10 Hz).
    /// </summary>
    public async Task JoinOrderBook(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        symbol = symbol.ToUpperInvariant();
        await Groups.AddToGroupAsync(Context.ConnectionId, $"ob:{symbol}");
        if (_obSubscriber is not null)
            await _obSubscriber.AddSubscriberAsync(symbol);
    }

    /// <summary>Stop receiving order book updates for <paramref name="symbol"/>.</summary>
    public async Task LeaveOrderBook(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        symbol = symbol.ToUpperInvariant();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"ob:{symbol}");
        if (_obSubscriber is not null)
            await _obSubscriber.RemoveSubscriberAsync(symbol);
    }

    // ── Legacy system vitals (WPF dashboard) ─────────────────────────────────

    public async Task UpdateSystemVitals(double protection, double investment, double fitness)
    {
        await Clients.All.SendAsync("ReceiveSystemVitals", new { protection, investment, fitness });
    }
}
