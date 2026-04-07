using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public sealed class LivePositionLifecycleService
{
    private static readonly ConcurrentDictionary<Guid, DateTime> s_lastProcessedBarByTradeId = new();

    private readonly ITradeRepository _tradeRepository;
    private readonly IMarketDataService _marketDataService;
    private readonly IExchangeService _exchangeService;
    private readonly PositionSupervisorService _positionSupervisor;
    private readonly ILogger<LivePositionLifecycleService> _logger;

    public LivePositionLifecycleService(
        ITradeRepository tradeRepository,
        IMarketDataService marketDataService,
        IExchangeService exchangeService,
        PositionSupervisorService positionSupervisor,
        ILogger<LivePositionLifecycleService> logger)
    {
        _tradeRepository = tradeRepository;
        _marketDataService = marketDataService;
        _exchangeService = exchangeService;
        _positionSupervisor = positionSupervisor;
        _logger = logger;
    }

    public async Task<int> SuperviseOpenTradesAsync(CancellationToken ct)
    {
        var openTrades = await _tradeRepository.GetOpenAsync(64);
        var supervisedCount = 0;

        foreach (var trade in openTrades)
        {
            ct.ThrowIfCancellationRequested();

            if (!ShouldSupervise(trade))
            {
                if (trade.IsClosed)
                {
                    s_lastProcessedBarByTradeId.TryRemove(trade.Id, out _);
                }

                continue;
            }

            if (await SuperviseTradeAsync(trade, ct))
            {
                supervisedCount++;
            }
        }

        return supervisedCount;
    }

    private async Task<bool> SuperviseTradeAsync(Trade trade, CancellationToken ct)
    {
        var candles = await _marketDataService.GetCandlesAsync(
            trade.Symbol,
            count: 3,
            market: ResolveMarketType(trade.Symbol),
            timeframe: "5m");

        if (candles.Count == 0)
        {
            return false;
        }

        var currentCandle = candles[^1];
        if (s_lastProcessedBarByTradeId.TryGetValue(trade.Id, out var lastProcessedBar) &&
            currentCandle.Timestamp <= lastProcessedBar)
        {
            return false;
        }

        var previousCandle = candles.Count > 1 ? candles[^2] : currentCandle;
        var atr = CalculateAtrProxy(candles, currentCandle.Close);
        var lifecycleAction = _positionSupervisor.AdvanceTrade(trade, currentCandle, previousCandle, atr);

        if (lifecycleAction == null)
        {
            await _tradeRepository.UpdateAsync(trade);
            s_lastProcessedBarByTradeId[trade.Id] = currentCandle.Timestamp;
            return true;
        }

        await HandleLifecycleActionAsync(trade, lifecycleAction, currentCandle, ct);
        s_lastProcessedBarByTradeId[trade.Id] = currentCandle.Timestamp;
        if (trade.IsClosed)
        {
            s_lastProcessedBarByTradeId.TryRemove(trade.Id, out _);
        }

        return true;
    }

    private async Task HandleLifecycleActionAsync(
        Trade trade,
        PositionLifecycleAction lifecycleAction,
        Candle currentCandle,
        CancellationToken ct)
    {
        _positionSupervisor.MarkClosing(trade, lifecycleAction.Reason);
        await _tradeRepository.UpdateAsync(trade);

        try
        {
            var exitSignal = CreateExitSignal(trade, lifecycleAction, currentCandle);
            var venueTrade = await _exchangeService.ExecuteOrderAsync(exitSignal);
            var exitFillQuantity = venueTrade.FilledQuantity > 0m
                ? venueTrade.FilledQuantity
                : venueTrade.Quantity;

            if (exitFillQuantity <= 0m)
            {
                _logger.LogInformation(
                    "Exit order accepted without fill for Trade {TradeId} ({Symbol}) via {Reason}",
                    trade.Id,
                    trade.Symbol,
                    lifecycleAction.Reason);
                return;
            }

            var exitPrice = venueTrade.EntryPrice > 0m
                ? venueTrade.EntryPrice
                : currentCandle.Close;

            _positionSupervisor.ApplyExitFill(
                trade,
                exitPrice,
                exitFillQuantity,
                lifecycleAction.Reason,
                venueTrade.EntryTime == default ? DateTime.UtcNow : venueTrade.EntryTime);

            await _tradeRepository.UpdateAsync(trade);
        }
        catch (Exception ex)
        {
            trade.LifecycleStatus = trade.Quantity > 0m
                ? TradeLifecycleStatus.Active
                : trade.LifecycleStatus;
            trade.ExitReason = $"{lifecycleAction.Reason} pending: {ex.Message}";
            trade.LastUpdatedTime = DateTime.UtcNow;
            await _tradeRepository.UpdateAsync(trade);

            _logger.LogError(
                ex,
                "Live position supervision failed while processing {ActionType} for Trade {TradeId} ({Symbol})",
                lifecycleAction.ActionType,
                trade.Id,
                trade.Symbol);
        }
    }

    private static Signal CreateExitSignal(Trade trade, PositionLifecycleAction lifecycleAction, Candle currentCandle)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ExecutionMode"] = string.IsNullOrWhiteSpace(trade.ExecutionMode) ? "REAL" : trade.ExecutionMode,
            ["PromotionStage"] = string.IsNullOrWhiteSpace(trade.PromotionStage) ? "LiveProbe" : trade.PromotionStage,
            ["ExitPolicyTag"] = string.IsNullOrWhiteSpace(trade.ExitPolicyTag) ? "TrendRunner" : trade.ExitPolicyTag,
            ["RequestedQuantity"] = lifecycleAction.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        return new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = trade.Symbol,
            Side = trade.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
            Price = lifecycleAction.ReferencePrice > 0m ? lifecycleAction.ReferencePrice : currentCandle.Close,
            Quantity = lifecycleAction.Quantity,
            Source = "LifecycleSupervisor",
            StrategyTag = trade.StrategyTag,
            RegimeTag = trade.RegimeTag,
            Timestamp = DateTime.UtcNow,
            Metadata = metadata
        };
    }

    private static bool ShouldSupervise(Trade trade)
        => trade.Quantity > 0m &&
           trade.LifecycleStatus is TradeLifecycleStatus.Active or TradeLifecycleStatus.PartiallyFilled or TradeLifecycleStatus.PartialExit;

    private static decimal CalculateAtrProxy(IReadOnlyList<Candle> candles, decimal currentPrice)
        => candles.Count >= 14
            ? candles.TakeLast(14).Average(c => c.High - c.Low)
            : currentPrice * 0.02m;

    private static string ResolveMarketType(string symbol)
    {
        if (symbol.Contains('-', StringComparison.OrdinalIgnoreCase) ||
            symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ||
            symbol.EndsWith("USD", StringComparison.OrdinalIgnoreCase))
        {
            return "Crypto";
        }

        return symbol.Contains("=", StringComparison.OrdinalIgnoreCase) ? "Forex" : "BIST";
    }
}
