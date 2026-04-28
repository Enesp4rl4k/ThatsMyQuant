using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

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
    private readonly IExecutionCostSnapshotCache? _liveCosts;

    public MarketExecutionProfileService(IExecutionCostSnapshotCache? liveCosts = null)
    {
        _liveCosts = liveCosts;
    }

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
    private static readonly IReadOnlyDictionary<(string Market, string Venue), MarketExecutionProfile> ProfilesByKey =
        Profiles.ToDictionary(
            profile => CreateProfileKey(profile.Market, profile.Venue),
            profile => profile);

    public MarketExecutionProfile ResolveForSymbol(string symbol, IDictionary<string, string>? metadata = null)
    {
        var market = ResolveMarket(symbol);
        var venue = ResolveVenue(market, metadata);
        return ProfilesByKey.TryGetValue(CreateProfileKey(market, venue), out var profile)
            ? profile
            : ProfilesByKey[CreateProfileKey(market, "Generic")];
    }

    public decimal ResolveSlippagePercent(string symbol, IDictionary<string, string>? metadata, decimal? overridePercent = null)
    {
        if (overridePercent is > 0)
            return overridePercent.Value;
        var live = _liveCosts?.GetSlippagePercent(symbol);
        if (live is > 0m)
            return live.Value;
        return ResolveForSymbol(symbol, metadata).SlippagePercent;
    }

    public decimal ResolveSpreadPercent(string symbol, IDictionary<string, string>? metadata, decimal? overridePercent = null)
    {
        if (overridePercent is > 0)
            return overridePercent.Value;
        var live = _liveCosts?.GetSpreadPercent(symbol);
        if (live is > 0m)
            return live.Value;
        return ResolveForSymbol(symbol, metadata).SpreadPercent;
    }

    public decimal ResolveCommissionPercent(string symbol, IDictionary<string, string>? metadata, decimal? overridePercent = null)
    {
        if (overridePercent is > 0)
            return overridePercent.Value;
        var live = _liveCosts?.GetTakerCommissionPercentPerSide(symbol);
        if (live is > 0m)
            return live.Value;
        return ResolveForSymbol(symbol, metadata).CommissionPercentPerSide;
    }

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

    /// <summary>
    /// Depth-aware execution price that walks a live <see cref="OrderBookSnapshot"/>
    /// to compute the volume-weighted fill price for a target quantity. Falls back
    /// to the profile-based <see cref="ApplyExecutionPrice"/> when the book is empty
    /// or insufficient. Entry buys walk asks (pay up), entry sells walk bids (pay
    /// down); exits invert.
    /// </summary>
    public decimal ApplyExecutionPriceWithBook(
        decimal price,
        decimal quantity,
        TradeSide side,
        bool isEntry,
        string symbol,
        OrderBookSnapshot? book,
        IDictionary<string, string>? metadata = null,
        decimal? overrideSlippagePercent = null,
        decimal? overrideSpreadPercent = null,
        Candle? candle = null)
    {
        if (!book.HasValue || quantity <= 0m)
        {
            return ApplyExecutionPrice(price, side, isEntry, symbol, metadata,
                overrideSlippagePercent, overrideSpreadPercent, candle);
        }

        var snap = book.Value;
        bool takesAsks = (side == TradeSide.Buy && isEntry) || (side == TradeSide.Sell && !isEntry);
        var levels = takesAsks ? snap.Asks : snap.Bids;
        if (levels is null || levels.Length == 0)
        {
            return ApplyExecutionPrice(price, side, isEntry, symbol, metadata,
                overrideSlippagePercent, overrideSpreadPercent, candle);
        }

        decimal remaining = quantity;
        decimal notional = 0m;
        decimal filled = 0m;
        foreach (var level in levels)
        {
            if (remaining <= 0m) break;
            if (level.Price <= 0m || level.Quantity <= 0m) continue;
            var take = Math.Min(remaining, level.Quantity);
            notional += take * level.Price;
            filled += take;
            remaining -= take;
        }

        if (filled <= 0m)
        {
            return ApplyExecutionPrice(price, side, isEntry, symbol, metadata,
                overrideSlippagePercent, overrideSpreadPercent, candle);
        }

        // Not enough depth to fill the full quantity — extrapolate the last level
        // price for the residual + a safety surcharge so the backtest/paper book-
        // walk result never optimistically under-counts slippage in thin markets.
        if (remaining > 0m)
        {
            var worst = levels[^1].Price;
            notional += remaining * worst * 1.002m;
            filled += remaining;
        }

        var vwap = notional / filled;
        return vwap;
    }

    /// <summary>
    /// Depth-aware slippage percentage estimate — walks the book to see how far
    /// the fill VWAP drifts from the top-of-book mid. Suitable for the cost-aware
    /// gate in pairs entries where we want a forward-looking number rather than
    /// the static profile figure.
    /// </summary>
    public decimal EstimateDepthSlippagePercent(
        string symbol,
        decimal quantity,
        TradeSide side,
        bool isEntry,
        OrderBookSnapshot? book)
    {
        if (!book.HasValue || quantity <= 0m) return 0m;
        var snap = book.Value;
        if (snap.Asks is null || snap.Bids is null ||
            snap.Asks.Length == 0 || snap.Bids.Length == 0) return 0m;

        var bestBid = snap.Bids[0].Price;
        var bestAsk = snap.Asks[0].Price;
        if (bestBid <= 0m || bestAsk <= 0m) return 0m;
        var mid = (bestBid + bestAsk) / 2m;

        var vwap = ApplyExecutionPriceWithBook(mid, quantity, side, isEntry, symbol, book);
        return Math.Abs(vwap - mid) / mid * 100m;
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

    /// <summary>Notional-based round-trip commission for a closed slice (entry + exit legs).</summary>
    public decimal CalculateRoundTripFees(
        decimal entryPrice,
        decimal exitPrice,
        decimal quantity,
        string symbol,
        IDictionary<string, string>? metadata = null,
        decimal? overrideCommissionPercent = null)
    {
        if (quantity <= 0m)
        {
            return 0m;
        }

        var commissionRate = ResolveCommissionPercent(symbol, metadata, overrideCommissionPercent) / 100m;
        return ((entryPrice * quantity) + (exitPrice * quantity)) * commissionRate;
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

    private static (string Market, string Venue) CreateProfileKey(string market, string venue)
        => (market.ToUpperInvariant(), venue.ToUpperInvariant());
}
