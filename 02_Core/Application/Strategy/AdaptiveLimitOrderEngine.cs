using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Adaptive Limit Order Engine — transitions from Market Taker to Limit Maker execution.
/// Uses momentum prediction and order book analysis to place limit orders at optimal levels,
/// capturing maker rebates and reducing execution costs.
///
/// Strategy:
/// - If momentum is predicted to continue (MicrosecondPredictionEngine signal),
///   place a limit order at Best Bid/Ask to queue for maker fills.
/// - If momentum is fading or urgency is high, fall back to aggressive limit or IOC.
/// - Automatically escalates to market order after timeout to avoid missed fills.
/// </summary>
public sealed class AdaptiveLimitOrderEngine
{
    private readonly ILogger<AdaptiveLimitOrderEngine> _logger;

    public AdaptiveLimitOrderEngine(ILogger<AdaptiveLimitOrderEngine> logger)
    {
        _logger = logger;
    }

    public sealed record OrderPlacement
    {
        public OrderType Type { get; init; }
        public decimal Price { get; init; }
        public decimal Quantity { get; init; }
        public TradeSide Side { get; init; }
        public TimeSpan TimeoutBeforeEscalation { get; init; }
        public string Reasoning { get; init; } = "";
    }

    public enum OrderType
    {
        LimitMaker,
        AggressiveLimit,
        MarketOrder
    }

    public sealed record MarketContext
    {
        public decimal BestBid { get; init; }
        public decimal BestAsk { get; init; }
        public decimal Spread { get; init; }
        public decimal SpreadBps { get; init; }
        public decimal BidDepth { get; init; }
        public decimal AskDepth { get; init; }
        public float MomentumSignal { get; init; }
        public decimal Volatility { get; init; }
        public decimal Volume24h { get; init; }
        public decimal MakerFeeRate { get; init; }
        public decimal TakerFeeRate { get; init; }
    }

    /// <summary>
    /// Determines optimal order placement strategy based on market microstructure.
    /// </summary>
    public OrderPlacement ComputeOptimalPlacement(
        Signal signal,
        MarketContext ctx)
    {
        if (ctx.BestBid <= 0 || ctx.BestAsk <= 0)
        {
            return FallbackToMarket(signal, signal.Price, "No valid order book data");
        }

        decimal midPrice = (ctx.BestBid + ctx.BestAsk) / 2m;
        bool isBuy = signal.Side == TradeSide.Buy;

        // Spread-based urgency: narrow spread = safe to post limit
        decimal spreadBps = ctx.SpreadBps > 0 ? ctx.SpreadBps : CalculateSpreadBps(ctx.BestBid, ctx.BestAsk);
        bool isSpreadNarrow = spreadBps <= 5m; // 5bps or less

        // Momentum alignment: momentum signal direction matches our side
        bool momentumAligned = isBuy
            ? ctx.MomentumSignal > 0.3f
            : ctx.MomentumSignal < -0.3f;

        // Depth imbalance: strong depth on our side = more time to fill
        decimal depthRatio = isBuy
            ? (ctx.BidDepth > 0 ? ctx.AskDepth / ctx.BidDepth : 1m)
            : (ctx.AskDepth > 0 ? ctx.BidDepth / ctx.AskDepth : 1m);
        bool depthFavorable = depthRatio < 1.5m;

        // Fee savings: only worth posting limit if spread > fee difference
        decimal feeSaving = ctx.TakerFeeRate - ctx.MakerFeeRate;
        bool feeSavingWorthIt = feeSaving > 0 && spreadBps > feeSaving * 10_000m;

        // Decision matrix
        if (isSpreadNarrow && momentumAligned && depthFavorable && feeSavingWorthIt)
        {
            return PassiveMaker(signal, isBuy, ctx, midPrice);
        }

        if (momentumAligned && depthFavorable)
        {
            return AggressiveLimit(signal, isBuy, ctx, midPrice);
        }

        if (!momentumAligned && ctx.Volatility > 0.02m)
        {
            return FallbackToMarket(signal, midPrice, "High volatility + adverse momentum");
        }

        // Default: aggressive limit slightly inside the spread
        return AggressiveLimit(signal, isBuy, ctx, midPrice);
    }

    /// <summary>
    /// Passive maker: post at best bid/ask to capture rebate.
    /// Longest fill time but lowest cost.
    /// </summary>
    private OrderPlacement PassiveMaker(Signal signal, bool isBuy, MarketContext ctx, decimal midPrice)
    {
        decimal price = isBuy ? ctx.BestBid : ctx.BestAsk;

        _logger.LogInformation(
            "AdaptiveLimitOrder: PASSIVE MAKER for {Symbol} @ {Price} (spread={Spread}bps, momentum={Momentum:F2})",
            signal.Symbol, price, ctx.SpreadBps, ctx.MomentumSignal);

        return new OrderPlacement
        {
            Type = OrderType.LimitMaker,
            Price = price,
            Quantity = signal.Quantity,
            Side = signal.Side,
            TimeoutBeforeEscalation = TimeSpan.FromSeconds(15),
            Reasoning = $"Passive maker at {'$'}{price:F4} — narrow spread ({ctx.SpreadBps:F1}bps), aligned momentum ({ctx.MomentumSignal:F2}), favorable depth"
        };
    }

    /// <summary>
    /// Aggressive limit: post inside the spread to improve fill probability.
    /// </summary>
    private OrderPlacement AggressiveLimit(Signal signal, bool isBuy, MarketContext ctx, decimal midPrice)
    {
        // Place 20% inside the spread from our side for higher fill probability
        decimal spreadStep = (ctx.BestAsk - ctx.BestBid) * 0.20m;
        decimal price = isBuy
            ? ctx.BestBid + spreadStep
            : ctx.BestAsk - spreadStep;

        // Round to a reasonable tick (avoid sub-cent precision issues)
        price = Math.Round(price, 8);

        _logger.LogInformation(
            "AdaptiveLimitOrder: AGGRESSIVE LIMIT for {Symbol} @ {Price} (20% inside spread)",
            signal.Symbol, price);

        return new OrderPlacement
        {
            Type = OrderType.AggressiveLimit,
            Price = price,
            Quantity = signal.Quantity,
            Side = signal.Side,
            TimeoutBeforeEscalation = TimeSpan.FromSeconds(8),
            Reasoning = $"Aggressive limit at {'$'}{price:F4} — stepping into spread for faster fill"
        };
    }

    private OrderPlacement FallbackToMarket(Signal signal, decimal price, string reason)
    {
        _logger.LogInformation(
            "AdaptiveLimitOrder: MARKET ORDER for {Symbol} — {Reason}",
            signal.Symbol, reason);

        return new OrderPlacement
        {
            Type = OrderType.MarketOrder,
            Price = price,
            Quantity = signal.Quantity,
            Side = signal.Side,
            TimeoutBeforeEscalation = TimeSpan.Zero,
            Reasoning = $"Market order — {reason}"
        };
    }

    private static decimal CalculateSpreadBps(decimal bid, decimal ask)
    {
        decimal mid = (bid + ask) / 2m;
        return mid > 0 ? (ask - bid) / mid * 10_000m : 0m;
    }
}
