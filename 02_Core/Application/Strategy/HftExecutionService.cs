using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

public sealed class HftExecutionService
{
    private readonly IExchangeService _exchangeService;
    private readonly ILogger<HftExecutionService> _logger;
    private readonly MarketExecutionProfileService _executionProfiles;
    private readonly LimitOrderExecutionConfig _limitConfig;
    private readonly ConcurrentDictionary<Guid, LimitOrderState> _activeLimitOrders = new();

    public HftExecutionService(
        IExchangeService exchangeService,
        ILogger<HftExecutionService> logger,
        MarketExecutionProfileService? executionProfiles = null,
        LimitOrderExecutionConfig? limitConfig = null)
    {
        _exchangeService = exchangeService;
        _logger = logger;
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
        _limitConfig = limitConfig ?? new LimitOrderExecutionConfig();
    }

    public async Task<Trade> ExecuteAsync(Signal signal, CancellationToken cancellationToken = default)
    {
        var market = ResolveMarket(signal.Symbol);
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
        var projectedLatencyMs = EstimateProjectedLatencyMs(signal, profile);
        var projectedSlippagePercent = EstimateProjectedSlippagePercent(signal, profile);
        var sessionAllowed = IsPreferredHftWindow(market, signal.Timestamp);
        if (!sessionAllowed)
        {
            _logger.LogWarning("HFT execution blocked for {Symbol}: outside preferred HFT window.", signal.Symbol);
            return new Trade
            {
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.Price,
                Quantity = 0,
                Status = TradeStatus.Failed,
                StrategyTag = signal.StrategyTag,
                RegimeTag = signal.RegimeTag,
                ExecutionMode = "REAL",
                PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe"),
                EntryTime = DateTime.UtcNow
            };
        }

        if (signal.Volume24h > 0 && signal.Volume24h < profile.MinHftVolume24h)
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: liquidity too weak ({Volume24h:N0}, needs {RequiredVolume:N0}) on {Venue}.",
                signal.Symbol,
                signal.Volume24h,
                profile.MinHftVolume24h,
                profile.Venue);
            return new Trade
            {
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.Price,
                Quantity = 0,
                Status = TradeStatus.Failed,
                StrategyTag = signal.StrategyTag,
                RegimeTag = signal.RegimeTag,
                ExecutionMode = "REAL",
                PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe"),
                EntryTime = DateTime.UtcNow
            };
        }

        if (projectedLatencyMs > profile.PreferredMaxLatencyMs * 1.35)
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: projected latency {ProjectedLatency:F0}ms exceeds venue budget {LatencyBudget}ms.",
                signal.Symbol,
                projectedLatencyMs,
                profile.PreferredMaxLatencyMs);
            return new Trade
            {
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.Price,
                Quantity = 0,
                Status = TradeStatus.Failed,
                StrategyTag = signal.StrategyTag,
                RegimeTag = signal.RegimeTag,
                ExecutionMode = "REAL",
                PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe"),
                EntryTime = DateTime.UtcNow
            };
        }

        if (projectedSlippagePercent > profile.SlippagePercent * 1.75m)
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: projected slippage {ProjectedSlippage:F4}% exceeds guardrail {SlippageBudget:F4}%.",
                signal.Symbol,
                projectedSlippagePercent,
                profile.SlippagePercent * 1.75m);
            return new Trade
            {
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.Price,
                Quantity = 0,
                Status = TradeStatus.Failed,
                StrategyTag = signal.StrategyTag,
                RegimeTag = signal.RegimeTag,
                ExecutionMode = "REAL",
                PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe"),
                EntryTime = DateTime.UtcNow
            };
        }

        var stopwatch = Stopwatch.StartNew();
        var trade = await _exchangeService.ExecuteOrderAsync(signal);
        stopwatch.Stop();

        trade.ExecutionMode = "REAL";
        trade.PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe");
        trade.ExecutionLatencyMs = stopwatch.ElapsedMilliseconds;
        trade.EntrySlippagePercent = signal.Price > 0
            ? Math.Abs((trade.EntryPrice - signal.Price) / signal.Price) * 100m
            : 0m;

        _logger.LogInformation(
            "HFT execution completed for {Symbol} via {Venue}. Latency={Latency}ms, Slippage={Slippage:F4}%",
            signal.Symbol,
            profile.Venue,
            trade.ExecutionLatencyMs,
            trade.EntrySlippagePercent);

        return trade;
    }

    public async Task<Trade> ExecuteLimitOrderAsync(
        Signal signal,
        OrderBookSnapshot? orderBook = null,
        CancellationToken cancellationToken = default)
    {
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
        var urgency = ComputeUrgency(signal);

        if (urgency >= (double)_limitConfig.UrgencyThreshold)
        {
            _logger.LogInformation(
                "Urgency {Urgency:F2} exceeds threshold {Threshold:F2} for {Symbol}. Falling back to market execution.",
                urgency, (double)_limitConfig.UrgencyThreshold, signal.Symbol);
            return await ExecuteAsync(signal, cancellationToken);
        }

        var limitPrice = ComputeLimitPrice(signal, orderBook, profile, urgency);
        var spreadPercent = ComputeSpreadPercent(orderBook, profile);

        if (spreadPercent > (double)_limitConfig.MaxSpreadTolerancePercent)
        {
            _logger.LogWarning(
                "Spread {Spread:F4}% exceeds tolerance {Tolerance:F4}% for {Symbol}. Falling back to market execution.",
                spreadPercent, (double)_limitConfig.MaxSpreadTolerancePercent, signal.Symbol);
            return await ExecuteAsync(signal, cancellationToken);
        }

        var volumeAhead = orderBook.HasValue
            ? CalculateVolumeAhead(orderBook.Value, limitPrice, signal.Side)
            : await _exchangeService.GetQueuePositionAsync(signal.Symbol, limitPrice, signal.Side);

        var queuePositionPercentile = EstimateQueuePositionPercentile(volumeAhead, orderBook, signal.Side);

        if (queuePositionPercentile < (double)_limitConfig.MinQueuePositionPercentile)
        {
            _logger.LogInformation(
                "Queue position {Percentile:F2}% below minimum {Min:F2}% for {Symbol}. Adjusting price.",
                queuePositionPercentile, (double)_limitConfig.MinQueuePositionPercentile, signal.Symbol);
            limitPrice = AdjustPriceForQueuePosition(limitPrice, signal.Side, profile);
        }

        var adjustedSignal = CloneSignal(signal);
        adjustedSignal.Price = limitPrice;
        adjustedSignal.ExecutionAction = signal.Side == TradeSide.Buy
            ? ExecutionActionType.LimitFrontRunBuy
            : ExecutionActionType.LimitFrontRunSell;

        var stopwatch = Stopwatch.StartNew();
        var trade = await _exchangeService.ExecuteOrderAsync(adjustedSignal);
        stopwatch.Stop();

        var orderState = new LimitOrderState
        {
            OrderId = trade.Id,
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            LimitPrice = limitPrice,
            Urgency = urgency,
            QueuePositionPercentile = queuePositionPercentile,
            VolumeAhead = volumeAhead,
            SubmittedAt = DateTime.UtcNow,
            RetryCount = 0
        };
        _activeLimitOrders[trade.Id] = orderState;

        trade.ExecutionMode = "LIMIT_MAKER";
        trade.PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe");
        trade.ExecutionLatencyMs = stopwatch.ElapsedMilliseconds;
        trade.EntrySlippagePercent = signal.Price > 0
            ? Math.Abs((trade.EntryPrice - signal.Price) / signal.Price) * 100m
            : 0m;
        trade.IsBreakevenArmed = false;

        _logger.LogInformation(
            "Limit order placed for {Symbol} @ {LimitPrice:F8}. Urgency={Urgency:F2}, QueuePos={QueuePos:F2}%, VolumeAhead={VolumeAhead:F4}",
            signal.Symbol, limitPrice, urgency, queuePositionPercentile, volumeAhead);

        return trade;
    }

    public async Task<Trade> ManageLimitOrderAsync(
        Guid orderId,
        Signal signal,
        OrderBookSnapshot? orderBook = null,
        CancellationToken cancellationToken = default)
    {
        if (!_activeLimitOrders.TryGetValue(orderId, out var orderState))
        {
            _logger.LogWarning("No active limit order found for {OrderId}", orderId);
            return new Trade { Id = orderId, Status = TradeStatus.Failed };
        }

        var ageMs = (DateTime.UtcNow - orderState.SubmittedAt).TotalMilliseconds;

        if (orderState.RetryCount >= _limitConfig.MaxRetries)
        {
            _logger.LogInformation(
                "Max retries ({MaxRetries}) reached for order {OrderId} on {Symbol}. Cancelling.",
                _limitConfig.MaxRetries, orderId, signal.Symbol);
            _activeLimitOrders.TryRemove(orderId, out _);
            return new Trade { Id = orderId, Status = TradeStatus.Rejected, LifecycleStatus = TradeLifecycleStatus.Cancelled };
        }

        if (ageMs > _limitConfig.MaxOrderAgeMs)
        {
            _logger.LogInformation(
                "Order {OrderId} on {Symbol} aged {AgeMs:F0}ms. Retrying with adjusted price.",
                orderId, signal.Symbol, ageMs);

            var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
            var fadedPrice = FadeLimitPrice(orderState.LimitPrice, signal.Side, orderState.RetryCount, _limitConfig);

            var adjustedSignal = CloneSignal(signal);
            adjustedSignal.Price = fadedPrice;
            var trade = await _exchangeService.ExecuteOrderAsync(adjustedSignal);

            orderState = orderState with
            {
                LimitPrice = fadedPrice,
                RetryCount = orderState.RetryCount + 1,
                SubmittedAt = DateTime.UtcNow
            };
            _activeLimitOrders[orderId] = orderState;

            trade.ExecutionMode = "LIMIT_MAKER";
            trade.ExecutionLatencyMs = 0;

            _logger.LogInformation(
                "Order {OrderId} repriced to {NewPrice:F8} (retry {Retry}/{MaxRetries})",
                orderId, fadedPrice, orderState.RetryCount, _limitConfig.MaxRetries);

            return trade;
        }

        return new Trade { Id = orderId, Status = TradeStatus.Queued, LifecycleStatus = TradeLifecycleStatus.Accepted };
    }

    public bool TryCancelLimitOrder(Guid orderId)
    {
        var removed = _activeLimitOrders.TryRemove(orderId, out _);
        if (removed)
        {
            _logger.LogInformation("Cancelled limit order {OrderId}", orderId);
        }
        return removed;
    }

    public IReadOnlyCollection<LimitOrderState> GetActiveOrders() => _activeLimitOrders.Values.ToList().AsReadOnly();

    private static decimal CalculateVolumeAhead(OrderBookSnapshot book, decimal price, TradeSide side)
    {
        decimal volumeAhead = 0;
        var levels = side == TradeSide.Buy ? book.Bids : book.Asks;
        foreach (var level in levels)
        {
            if (side == TradeSide.Buy)
            {
                if (level.Price > price) volumeAhead += level.Quantity;
                else if (level.Price == price)
                {
                    volumeAhead += level.Quantity * 0.5m;
                    break;
                }
            }
            else
            {
                if (level.Price < price) volumeAhead += level.Quantity;
                else if (level.Price == price)
                {
                    volumeAhead += level.Quantity * 0.5m;
                    break;
                }
            }
        }
        return volumeAhead;
    }

    private static double ComputeSpreadPercent(OrderBookSnapshot? orderBook, MarketExecutionProfile profile)
    {
        if (orderBook.HasValue && orderBook.Value.Bids.Length > 0 && orderBook.Value.Asks.Length > 0)
        {
            var bestBid = orderBook.Value.Bids[0].Price;
            var bestAsk = orderBook.Value.Asks[0].Price;
            var mid = (bestBid + bestAsk) / 2;
            return mid > 0 ? (double)((bestAsk - bestBid) / mid * 100) : (double)profile.SpreadPercent;
        }
        return (double)profile.SpreadPercent;
    }

    private static decimal ComputeLimitPrice(
        Signal signal,
        OrderBookSnapshot? orderBook,
        MarketExecutionProfile profile,
        double urgency,
        LimitOrderExecutionConfig config)
    {
        if (orderBook.HasValue && orderBook.Value.Bids.Length > 0 && orderBook.Value.Asks.Length > 0)
        {
            var bestBid = orderBook.Value.Bids[0].Price;
            var bestAsk = orderBook.Value.Asks[0].Price;
            var midPrice = (bestBid + bestAsk) / 2;
            var spread = bestAsk - bestBid;

            if (signal.Side == TradeSide.Buy)
            {
                var offset = spread * (0.3m + (decimal)urgency * 0.4m);
                return Math.Min(bestAsk - config.PriceImprovementTicks, midPrice + offset);
            }
            else
            {
                var offset = spread * (0.3m + (decimal)urgency * 0.4m);
                return Math.Max(bestBid + config.PriceImprovementTicks, midPrice - offset);
            }
        }

        var tickSize = profile.SpreadPercent / 100m * signal.Price;
        return signal.Side == TradeSide.Buy
            ? signal.Price - tickSize * (1m + (decimal)urgency)
            : signal.Price + tickSize * (1m + (decimal)urgency);
    }

    private decimal ComputeLimitPrice(Signal signal, OrderBookSnapshot? orderBook, MarketExecutionProfile profile, double urgency)
    {
        return ComputeLimitPrice(signal, orderBook, profile, urgency, _limitConfig);
    }

    private static decimal AdjustPriceForQueuePosition(
        decimal currentPrice,
        TradeSide side,
        MarketExecutionProfile profile,
        LimitOrderExecutionConfig config)
    {
        var tickSize = profile.SpreadPercent / 100m * currentPrice;
        var improvement = config.PriceImprovementTicks;

        return side == TradeSide.Buy
            ? currentPrice + Math.Max(tickSize, improvement)
            : currentPrice - Math.Max(tickSize, improvement);
    }

    private decimal AdjustPriceForQueuePosition(decimal currentPrice, TradeSide side, MarketExecutionProfile profile)
    {
        return AdjustPriceForQueuePosition(currentPrice, side, profile, _limitConfig);
    }

    private static decimal FadeLimitPrice(decimal currentPrice, TradeSide side, int retryCount, LimitOrderExecutionConfig config)
    {
        var fadeAmount = config.FadeRatePerRetry * currentPrice * retryCount;
        return side == TradeSide.Buy
            ? currentPrice + fadeAmount
            : currentPrice - fadeAmount;
    }

    private static double ComputeUrgency(Signal signal)
    {
        var confidenceFactor = signal.Confidence / 100.0;
        var ofiFactor = signal.OrderFlowImbalance.HasValue
            ? Math.Abs(signal.OrderFlowImbalance.Value)
            : 0.5;
        var wallAgeFactor = signal.WallAgeMs.HasValue
            ? Math.Min(1.0, signal.WallAgeMs.Value / 5000.0)
            : 0.5;

        return Math.Clamp(confidenceFactor * 0.4 + ofiFactor * 0.35 + wallAgeFactor * 0.25, 0.0, 1.0);
    }

    private static double EstimateQueuePositionPercentile(
        decimal volumeAhead,
        OrderBookSnapshot? orderBook,
        TradeSide side)
    {
        if (!orderBook.HasValue || volumeAhead <= 0) return 0.5;

        var levels = side == TradeSide.Buy ? orderBook.Value.Bids : orderBook.Value.Asks;
        var totalVolume = levels.Sum(l => l.Quantity);

        return totalVolume > 0
            ? 1.0 - (double)(volumeAhead / totalVolume)
            : 0.5;
    }

    private static Signal CloneSignal(Signal original)
    {
        return new Signal
        {
            Id = original.Id,
            Source = original.Source,
            Symbol = original.Symbol,
            Side = original.Side,
            Price = original.Price,
            Status = original.Status,
            RetryCount = original.RetryCount,
            Quantity = original.Quantity,
            Confidence = original.Confidence,
            Reasoning = original.Reasoning,
            StrategyTag = original.StrategyTag,
            RegimeTag = original.RegimeTag,
            MarketType = original.MarketType,
            Exchange = original.Exchange,
            Timestamp = original.Timestamp,
            Volume24h = original.Volume24h,
            OrderFlowImbalance = original.OrderFlowImbalance,
            BestBid = original.BestBid,
            BestAsk = original.BestAsk,
            WallAgeMs = original.WallAgeMs,
            ExecutionAction = original.ExecutionAction,
            PriceActionSignal = original.PriceActionSignal,
            ReasonFlags = original.ReasonFlags,
            Metadata = new Dictionary<string, string>(original.Metadata)
        };
    }

    private static string ResolveMarket(string symbol)
    {
        if (symbol.Contains("-", StringComparison.OrdinalIgnoreCase)) return "Crypto";
        if (symbol.Contains("=", StringComparison.OrdinalIgnoreCase)) return "Forex";
        return "BIST";
    }

    private static bool IsPreferredHftWindow(string market, DateTime timestamp)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var turkeyTime = timestamp.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp.ToUniversalTime(), "Turkey Standard Time");

        return turkeyTime.Hour is >= 10 and < 13 or >= 15 and < 18;
    }

    private static double EstimateProjectedLatencyMs(Signal signal, MarketExecutionProfile profile)
    {
        var confidencePenalty = Math.Max(0, 80 - signal.Confidence) * 0.8;
        var volumeRatio = signal.Volume24h <= 0 || profile.MinHftVolume24h <= 0
            ? 1m
            : signal.Volume24h / profile.MinHftVolume24h;
        var liquidityPenalty = volumeRatio >= 2m ? 0 : (double)((2m - volumeRatio) * 35m);
        return profile.PreferredMaxLatencyMs + confidencePenalty + liquidityPenalty;
    }

    private static decimal EstimateProjectedSlippagePercent(Signal signal, MarketExecutionProfile profile)
    {
        var volumeRatio = signal.Volume24h <= 0 || profile.MinHftVolume24h <= 0
            ? 1m
            : signal.Volume24h / profile.MinHftVolume24h;
        var multiplier = volumeRatio >= 2m ? 0.85m :
            volumeRatio >= 1.25m ? 1.00m :
            volumeRatio >= 1m ? 1.35m : 1.80m;
        return profile.SlippagePercent * multiplier;
    }
}

public record LimitOrderState
{
    public Guid OrderId { get; init; }
    public Guid SignalId { get; init; }
    public string Symbol { get; init; } = "";
    public TradeSide Side { get; init; }
    public decimal LimitPrice { get; init; }
    public double Urgency { get; init; }
    public double QueuePositionPercentile { get; init; }
    public decimal VolumeAhead { get; init; }
    public DateTime SubmittedAt { get; init; }
    public int RetryCount { get; init; }
}
