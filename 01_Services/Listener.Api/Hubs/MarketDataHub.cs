using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using ThatsMyQuant.Contracts.Models;
using Listener.Api.Services;

namespace Listener.Api.Hubs;

public partial class MarketDataHub : Hub
{
    private readonly MarketDataBroadcastService _broadcaster;
    private const int MaxSymbolLength = 20;

    /// <summary>
    /// Validates symbol format: only uppercase letters, digits, and hyphens (e.g. BTCUSDT, BTC-USDT).
    /// </summary>
    [GeneratedRegex(@"^[A-Z0-9\-]{2,20}$")]
    private static partial Regex ValidSymbolRegex();

    public MarketDataHub(MarketDataBroadcastService broadcaster)
    {
        _broadcaster = broadcaster;
    }

    public async Task SubscribeToMarket(string symbol)
    {
        var sanitized = ValidateAndNormalizeSymbol(symbol);
        await Groups.AddToGroupAsync(Context.ConnectionId, sanitized);
        await _broadcaster.SubscribeSymbolAsync(sanitized);
    }

    public async Task UnsubscribeFromMarket(string symbol)
    {
        var sanitized = ValidateAndNormalizeSymbol(symbol);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sanitized);
        await _broadcaster.UnsubscribeSymbolAsync(sanitized);
    }

    public async Task UpdateSystemVitals(double protection, double investment, double fitness)
    {
        // Clamp values to reasonable range to prevent garbage data broadcast
        protection = Math.Clamp(protection, 0, 100);
        investment = Math.Clamp(investment, 0, 100);
        fitness = Math.Clamp(fitness, 0, 100);

        await Clients.All.SendAsync("ReceiveSystemVitals", new { protection, investment, fitness });
    }

    private static string ValidateAndNormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new HubException("Symbol cannot be empty.");

        var trimmed = symbol.Trim().ToUpperInvariant();

        if (trimmed.Length > MaxSymbolLength)
            throw new HubException($"Symbol exceeds maximum length of {MaxSymbolLength}.");

        if (!ValidSymbolRegex().IsMatch(trimmed))
            throw new HubException("Invalid symbol format. Only uppercase letters, digits, and hyphens are allowed.");

        return trimmed;
    }
}
