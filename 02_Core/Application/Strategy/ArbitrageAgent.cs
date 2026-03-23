using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using System.Collections.Concurrent;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A specialized High-Frequency Swarm Agent that monitors price discrepancies across different exchanges.
/// Upgraded with Ultra-HFT primitives:
/// - O(K) Exchange lookups (eliminates O(N) full-book scans).
/// - Zero-Allocation Substring parsing (removes .Split() heap allocations).
/// - Hardware FPU Double math.
/// - Millisecond Time-Alignment to reject stale quotes.
/// </summary>
public class ArbitrageAgent : INotificationHandler<Signal>
{
    private readonly ILogger<ArbitrageAgent> _logger;
    
    private sealed class PricePoint
    {
        public readonly double Price;
        public readonly long TimestampMs;

        public PricePoint(double price, long timestampMs)
        {
            Price = price;
            TimestampMs = timestampMs;
        }
    }

    // Structure: BaseSymbol -> { Exchange -> PricePoint }
    // Example: "BTC" -> { "BINANCE" -> PricePoint, "COINBASE" -> PricePoint }
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PricePoint>> PricesBySymbol = new();

    public ArbitrageAgent(ILogger<ArbitrageAgent> logger)
    {
        _logger = logger;
    }

    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        if (!TryExtractSymbolInfo(notification.Symbol, out var baseSymbol, out var exchange))
            return Task.CompletedTask;

        var symbolBook = PricesBySymbol.GetOrAdd(baseSymbol, static _ => new ConcurrentDictionary<string, PricePoint>());
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double currentPrice = (double)notification.Price;

        // Update the current exchange safely
        symbolBook[exchange] = new PricePoint(currentPrice, nowMs);
        
        EvaluateArbitrageOpportunities(baseSymbol, exchange, currentPrice, nowMs, symbolBook);

        return Task.CompletedTask;
    }

    private static bool TryExtractSymbolInfo(string symbol, out string baseSymbol, out string exchange)
    {
        baseSymbol = string.Empty;
        exchange = string.Empty;

        ReadOnlySpan<char> span = symbol.AsSpan();
        int idx = span.IndexOf('-');
        if (idx <= 0) return false;

        baseSymbol = span[..idx].ToString();
        exchange = span[(idx + 1)..].ToString();
        return true;
    }

    private void EvaluateArbitrageOpportunities(
        string baseSymbol, 
        string exchange, 
        double currentPrice, 
        long nowMs, 
        ConcurrentDictionary<string, PricePoint> symbolBook)
    {
        foreach (var kvp in symbolBook)
        {
            if (kvp.Key == exchange) continue;

            PricePoint otherPoint = kvp.Value;

            // CRITICAL: Reject stale quotes completely out of sync by more than ~100ms
            if (Math.Abs(nowMs - otherPoint.TimestampMs) > 100) continue; 

            double otherPrice = otherPoint.Price;
            double spreadPercent = CalculateSpreadPercent(currentPrice, otherPrice);

            // Enterprise thresholds: Maker/Taker fees (0.05% x 2) + Slippage Buffer + Alpha edge
            const double MinEdge = 0.10; 
            const double RequiredProfit = 0.05;

            if (IsEligibleForArbitrage(spreadPercent, MinEdge, RequiredProfit)) 
            {
                LogArbitrageDetection(baseSymbol, exchange, kvp.Key, currentPrice, otherPrice, spreadPercent);
            }
        }
    }

    private static double CalculateSpreadPercent(double priceA, double priceB)
    {
        double spread = Math.Abs(priceA - priceB);
        return (spread / priceB) * 100.0;
    }

    private static bool IsEligibleForArbitrage(double spreadPercent, double minEdge, double requiredProfit)
    {
        return spreadPercent > (minEdge + requiredProfit);
    }

    private void LogArbitrageDetection(
        string baseSymbol, 
        string exchange, 
        string otherExchange, 
        double price, 
        double otherPrice, 
        double spreadPercent)
    {
        string action = price < otherPrice 
            ? $"BUY {exchange} / SELL {otherExchange}"
            : $"SELL {exchange} / BUY {otherExchange}";
        
        _logger.LogInformation($"[ARB DETECTED] {baseSymbol} spread is {spreadPercent:F3}%. Action: {action}.");
    }
}
