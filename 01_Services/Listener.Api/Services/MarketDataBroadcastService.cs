using Microsoft.AspNetCore.SignalR;
using Listener.Api.Hubs;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api.Services;

public class MarketDataBroadcastService : BackgroundService
{
    private readonly IStreamingMarketDataService _streamingService;
    private readonly IHubContext<MarketDataHub> _hubContext;
    private readonly ILogger<MarketDataBroadcastService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _symbolSubscribers = new();

    public MarketDataBroadcastService(
        IStreamingMarketDataService streamingService,
        IHubContext<MarketDataHub> hubContext,
        ILogger<MarketDataBroadcastService> logger)
    {
        _streamingService = streamingService;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _streamingService.OnTickReceived += async (tick) =>
        {
            // Only broadcast to the specific symbol group
            await _hubContext.Clients.Group(tick.Symbol).SendAsync("ReceiveTick", tick, stoppingToken);
        };

        // Connect to a few major indices for the demo
        var symbols = new[] { "BTC-USDT", "ETH-USDT", "SOL-USDT" };
        await _streamingService.ConnectAsync(symbols, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
        
        await _streamingService.DisconnectAsync();
    }

    public async Task SubscribeSymbolAsync(string symbol)
    {
        _symbolSubscribers.AddOrUpdate(symbol, 1, (k, v) => v + 1);
        await _streamingService.SubscribeAsync(symbol);
        _logger.LogInformation("Broadcaster: Subscribed to {Symbol}. Total listeners: {Count}", symbol, _symbolSubscribers[symbol]);
    }

    public async Task UnsubscribeSymbolAsync(string symbol)
    {
        if (_symbolSubscribers.TryGetValue(symbol, out int count))
        {
            if (count <= 1)
            {
                _symbolSubscribers.TryRemove(symbol, out _);
                await _streamingService.UnsubscribeAsync(symbol);
                _logger.LogInformation("Broadcaster: Zero listeners for {Symbol}. Stopped stream.", symbol);
            }
            else
            {
                _symbolSubscribers.TryUpdate(symbol, count - 1, count);
            }
        }
    }
}
