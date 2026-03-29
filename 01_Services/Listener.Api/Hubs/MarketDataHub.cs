using Microsoft.AspNetCore.SignalR;
using ThatsMyQuant.Contracts.Models;
using Listener.Api.Services;

namespace Listener.Api.Hubs;

public class MarketDataHub : Hub
{
    private readonly MarketDataBroadcastService _broadcaster;

    public MarketDataHub(MarketDataBroadcastService broadcaster)
    {
        _broadcaster = broadcaster;
    }

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

    public async Task UpdateSystemVitals(double protection, double investment, double fitness)
    {
        await Clients.All.SendAsync("ReceiveSystemVitals", new { protection, investment, fitness });
    }
}
