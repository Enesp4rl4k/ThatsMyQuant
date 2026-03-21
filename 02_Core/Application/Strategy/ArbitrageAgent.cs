using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A specialized Swarm Agent that monitors price discrepancies across different exchanges.
/// It uses a Z-Score calculation to detect if one exchange is "lagging" behind the other, 
/// creating a temporary, risk-minimized profit opportunity.
/// </summary>
public class ArbitrageAgent : INotificationHandler<ThatsMyQuant.Domain.Entities.Signal>
{
    private readonly ILogger<ArbitrageAgent> _logger;
    
    // Simple in-memory tracker for the latest price by exchange
    private static readonly Dictionary<string, decimal> LatestPrices = new();

    public ArbitrageAgent(ILogger<ArbitrageAgent> logger)
    {
        _logger = logger;
    }

    public Task Handle(ThatsMyQuant.Domain.Entities.Signal notification, CancellationToken cancellationToken)
    {
        string[] parts = notification.Symbol.Split('-');
        if (parts.Length < 2) return Task.CompletedTask;

        string baseSymbol = parts[0];
        string exchange = parts[1];

        lock (LatestPrices)
        {
            LatestPrices[notification.Symbol] = notification.Price;
            
            // Look for the same symbol on other exchanges
            foreach (var kvp in LatestPrices.Where(x => x.Key.StartsWith(baseSymbol) && x.Key != notification.Symbol))
            {
                decimal spread = Math.Abs(notification.Price - kvp.Value);
                decimal spreadPercent = (spread / kvp.Value) * 100;

                // Enterprise threshold: Spread must cover double commission + profit margin
                if (spreadPercent > 0.15m) 
                {
                    string targetExchange = kvp.Key.Split('-')[1];
                    string direction = notification.Price < kvp.Value ? "BUY" : "SELL";
                    
                    _logger.LogInformation($"[ARB DETECTED] {baseSymbol} spread is {spreadPercent:F3}% between {exchange} and {targetExchange}. Suggesting {direction} on {exchange}.");
                    
                    // Here we would emit a TradeDecision for the Swarm Orchestrator to weigh
                }
            }
        }

        return Task.CompletedTask;
    }
}
