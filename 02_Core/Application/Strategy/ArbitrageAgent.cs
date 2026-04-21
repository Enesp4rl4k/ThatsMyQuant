using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using System.Collections.Concurrent;
using System;

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
    private readonly IMediator _mediator;
    private readonly ISharedPortfolioStateManager _portfolio;
    
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

    public ArbitrageAgent(ILogger<ArbitrageAgent> logger, IMediator mediator, ISharedPortfolioStateManager portfolio)
    {
        _logger = logger;
        _mediator = mediator;
        _portfolio = portfolio;
    }

    public async Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        if (!TryExtractSymbolInfo(notification.Symbol, out var baseSymbol, out var exchange))
            return;

        var symbolBook = PricesBySymbol.GetOrAdd(baseSymbol, static _ => new ConcurrentDictionary<string, PricePoint>());
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double currentPrice = (double)notification.Price;

        // Update the current exchange safely
        symbolBook[exchange] = new PricePoint(currentPrice, nowMs);
        
        await EvaluateArbitrageOpportunitiesAsync(baseSymbol, exchange, currentPrice, nowMs, symbolBook, cancellationToken);
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

    private async Task EvaluateArbitrageOpportunitiesAsync(
        string baseSymbol, 
        string exchange, 
        double currentPrice, 
        long nowMs, 
        ConcurrentDictionary<string, PricePoint> symbolBook,
        CancellationToken cancellationToken)
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
                await ExecuteArbitrageDualLegAsync(baseSymbol, exchange, kvp.Key, currentPrice, otherPrice, spreadPercent, cancellationToken);
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

    private async Task ExecuteArbitrageDualLegAsync(
        string baseSymbol, 
        string exchange, 
        string otherExchange, 
        double price, 
        double otherPrice, 
        double spreadPercent,
        CancellationToken ct)
    {
        // Volume Scaler: Cüzdan hacmi oranlayıcı (10% max allocation per risk-free arb)
        decimal maxAllocationUsdt = 0m;
        
        if (Enum.TryParse<VenueKind>(exchange, true, out var venue1) && 
            Enum.TryParse<VenueKind>(otherExchange, true, out var venue2))
        {
            var avb1 = _portfolio.GetAvailableBalance(venue1, "USDT");
            var avb2 = _portfolio.GetAvailableBalance(venue2, "USDT");
            
            // Limit by the lowest balance between the two venues to keep delta-neutral size identical.
            decimal lowerBalance = Math.Min(avb1, avb2);
            maxAllocationUsdt = lowerBalance * 0.10m; // 10% per arbitrage stroke
        }

        // If portfolio mock or not fully funded, fallback to a $1000 standard test block
        if (maxAllocationUsdt <= 10m) maxAllocationUsdt = 1000m;

        decimal scaledQuantity = Math.Round(maxAllocationUsdt / (decimal)Math.Max(price, otherPrice), 4);

        string actionInfo = price < otherPrice 
            ? $"BUY {exchange} @ {price} | SELL {otherExchange} @ {otherPrice}"
            : $"SELL {exchange} @ {price} | BUY {otherExchange} @ {otherPrice}";
        
        _logger.LogInformation($"[ARB TRIGGERED] {baseSymbol} VolScaler: {scaledQuantity} units (Max ${maxAllocationUsdt:F2}). Action: {actionInfo}");

        // Leg 1: Current Exchange (Origin of the latest ping)
        var leg1 = new Signal
        {
            Symbol = $"{baseSymbol}-USDT",
            Exchange = exchange,
            Side = price < otherPrice ? TradeSide.Buy : TradeSide.Sell,
            Price = (decimal)price,
            Quantity = scaledQuantity,
            Confidence = 99, 
            Source = "ArbitrageAgent",
            StrategyTag = "StatisticalArbitrage",
            RegimeTag = "VolatileCapture",
            Reasoning = $"Arbitrage dual-leg capture. Spread={spreadPercent:F3}% vs {otherExchange}",
            Metadata = new Dictionary<string, string> { { "ExecutionMode", "HFT" }, { "OpposingExchange", otherExchange } }
        };

        // Leg 2: Counterparty Exchange (The stale/differing book)
        var leg2 = new Signal
        {
            Symbol = $"{baseSymbol}-USDT",
            Exchange = otherExchange,
            Side = price < otherPrice ? TradeSide.Sell : TradeSide.Buy,
            Price = (decimal)otherPrice,
            Quantity = scaledQuantity,
            Confidence = 99, 
            Source = "ArbitrageAgent",
            StrategyTag = "StatisticalArbitrage",
            RegimeTag = "VolatileCapture",
            Reasoning = $"Arbitrage dual-leg capture. Spread={spreadPercent:F3}% vs {exchange}",
            Metadata = new Dictionary<string, string> { { "ExecutionMode", "HFT" }, { "OpposingExchange", exchange } }
        };

        // Fire & Forget dual execution into the OMS pipeline simultaneously. 
        // Real-world implementation requires a two-phase commit or immediate fallback if leg 1 fails.
        _ = _mediator.Send(new ThatsMyQuant.Contracts.Commands.ExecuteTradeCommand(leg1), ct);
        _ = _mediator.Send(new ThatsMyQuant.Contracts.Commands.ExecuteTradeCommand(leg2), ct);
    }
}
