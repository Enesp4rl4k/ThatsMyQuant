using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;

namespace ThatsMyQuant.Application.Strategy;

public sealed record MarketExecutionProfile(
    string Market,
    string Venue,
    decimal SlippagePercent,
    decimal SpreadPercent,
    decimal CommissionPercentPerSide,
    decimal MinHftVolume24h,
    long PreferredMaxLatencyMs);

public sealed class MarketExecutionProfileService
{
    private static readonly IReadOnlyList<MarketExecutionProfile> Profiles =
    [
        new("Crypto", "Binance", 0.08m, 0.03m, 0.04m, 1_000_000m, 120),
        new("Crypto", "Bybit", 0.09m, 0.035m, 0.055m, 900_000m, 135),
        new("Crypto", "Generic", 0.10m, 0.04m, 0.06m, 800_000m, 150),
        new("Forex", "Oanda", 0.02m, 0.015m, 0.015m, 250_000m, 90),
        new("Forex", "Generic", 0.025m, 0.018m, 0.02m, 250_000m, 95),
        new("BIST", "BIST", 0.06m, 0.025m, 0.05m, 400_000m, 180),
        new("BIST", "Generic", 0.06m, 0.025m, 0.05m, 400_000m, 180)
    ];

    public MarketExecutionProfile ResolveForSymbol(string symbol, IDictionary<string, string>? metadata = null)
    {
        var market = ResolveMarket(symbol);
        var venue = ResolveVenue(market, metadata);

        return Profiles.FirstOrDefault(profile =>
                   string.Equals(profile.Market, market, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(profile.Venue, venue, StringComparison.OrdinalIgnoreCase))
               ?? Profiles.First(profile =>
                   string.Equals(profile.Market, market, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(profile.Venue, "Generic", StringComparison.OrdinalIgnoreCase));
    }

    public decimal ResolveSlippagePercent(string symbol, IDictionary<string, string>? metadata, decimal? overridePercent = null)
        => overridePercent is > 0 ? overridePercent.Value : ResolveForSymbol(symbol, metadata).SlippagePercent;

    public decimal ResolveSpreadPercent(string symbol, IDictionary<string, string>? metadata, decimal? overridePercent = null)
        => overridePercent is > 0 ? overridePercent.Value : ResolveForSymbol(symbol, metadata).SpreadPercent;

    public decimal ResolveCommissionPercent(string symbol, IDictionary<string, string>? metadata, decimal? overridePercent = null)
        => overridePercent is > 0 ? overridePercent.Value : ResolveForSymbol(symbol, metadata).CommissionPercentPerSide;

    public decimal ApplyExecutionPrice(
        decimal price,
        TradeSide side,
        bool isEntry,
        string symbol,
        IDictionary<string, string>? metadata = null,
        decimal? overrideSlippagePercent = null,
        decimal? overrideSpreadPercent = null,
        Candle? candle = null)
    {
        var slippagePercent = ResolveSlippagePercent(symbol, metadata, overrideSlippagePercent);
        var spreadPercent = ResolveSpreadPercent(symbol, metadata, overrideSpreadPercent);
        var candleStress = candle == null || candle.Close == 0
            ? 1m
            : Math.Clamp((candle.High - candle.Low) / candle.Close * 25m, 1m, 2.5m);
        var slippage = (slippagePercent / 100m) * candleStress;
        var spreadHalf = (spreadPercent / 100m) / 2m;
        var cost = slippage + spreadHalf;

        var direction = (side == TradeSide.Buy && isEntry) || (side == TradeSide.Sell && !isEntry) ? 1m : -1m;
        return price * (1m + (direction * cost));
    }

    public decimal CalculateNetPnl(
        decimal entryPrice,
        decimal exitPrice,
        decimal quantity,
        TradeSide side,
        string symbol,
        IDictionary<string, string>? metadata = null,
        decimal? overrideCommissionPercent = null)
    {
        var direction = side == TradeSide.Sell ? -1m : 1m;
        var gross = (exitPrice - entryPrice) * quantity * direction;
        var commissionRate = ResolveCommissionPercent(symbol, metadata, overrideCommissionPercent) / 100m;
        var fees = ((entryPrice * quantity) + (exitPrice * quantity)) * commissionRate;
        return gross - fees;
    }

    private static string ResolveMarket(string symbol)
    {
        if (symbol.Contains("-", StringComparison.OrdinalIgnoreCase))
        {
            return "Crypto";
        }

        if (symbol.Contains("=", StringComparison.OrdinalIgnoreCase))
        {
            return "Forex";
        }

        return "BIST";
    }

    private static string ResolveVenue(string market, IDictionary<string, string>? metadata)
    {
        if (metadata != null)
        {
            if (metadata.TryGetValue("Venue", out var venue) && !string.IsNullOrWhiteSpace(venue))
            {
                return venue;
            }

            if (metadata.TryGetValue("Exchange", out var exchange) && !string.IsNullOrWhiteSpace(exchange))
            {
                return exchange;
            }
        }

        return market switch
        {
            "Crypto" => "Binance",
            "Forex" => "Oanda",
            "BIST" => "BIST",
            _ => "Generic"
        };
    }
}
