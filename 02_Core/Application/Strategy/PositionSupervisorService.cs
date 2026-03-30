using System.Globalization;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public enum PositionLifecycleActionType
{
    None,
    PartialExit,
    FullExit
}

public sealed record PositionLifecycleAction(
    PositionLifecycleActionType ActionType,
    string Reason,
    decimal Quantity,
    decimal ReferencePrice);

public sealed class PositionSupervisorService
{
    public Trade CreateSubmittedTrade(Signal signal)
    {
        var requestedQuantity = signal.Quantity > 0m
            ? signal.Quantity
            : TryGetDecimal(signal, "RequestedQuantity");
        var executionMode = TryGetString(signal, "ExecutionMode") ?? "REAL";
        var promotionStage = TryGetString(signal, "PromotionStage") ?? "LiveProbe";
        var now = DateTime.UtcNow;

        return new Trade
        {
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            EntryPrice = signal.Price,
            MarkPrice = signal.Price,
            Status = TradeStatus.Pending,
            LifecycleStatus = TradeLifecycleStatus.Submitted,
            StrategyTag = string.IsNullOrWhiteSpace(signal.StrategyTag) ? "TrendConfluence" : signal.StrategyTag,
            RegimeTag = string.IsNullOrWhiteSpace(signal.RegimeTag) ? "Unknown" : signal.RegimeTag,
            ExitPolicyTag = TryGetString(signal, "ExitPolicyTag") ?? "TrendRunner",
            ExecutionMode = executionMode,
            PromotionStage = promotionStage,
            RequestedQuantity = requestedQuantity,
            RemainingQuantity = requestedQuantity,
            PositionSizePercent = TryGetDecimal(signal, "RequestedPositionFraction"),
            StopLoss = TryGetDecimal(signal, "StopLoss"),
            TakeProfit = TryGetDecimal(signal, "TakeProfit"),
            PartialTakeProfit = TryGetDecimal(signal, "PartialTakeProfit"),
            PartialExitFraction = TryGetDecimal(signal, "PartialExitFraction"),
            BreakevenTriggerFraction = TryGetDecimal(signal, "BreakevenTriggerFraction", 0.50m),
            MaxHoldingBars = TryGetInt(signal, "MaxHoldingBars"),
            SubmittedTime = now,
            LastUpdatedTime = now,
            EntryTime = now
        };
    }

    public void MarkAccepted(Trade trade, DateTime? atUtc = null)
    {
        trade.LifecycleStatus = TradeLifecycleStatus.Accepted;
        trade.LastUpdatedTime = atUtc ?? DateTime.UtcNow;
    }

    public void ApplyExecutionFill(Trade trackedTrade, Trade venueTrade)
    {
        var now = DateTime.UtcNow;
        var executedQuantity = venueTrade.Quantity > 0m
            ? venueTrade.Quantity
            : trackedTrade.RequestedQuantity;
        var requestedQuantity = trackedTrade.RequestedQuantity > 0m
            ? trackedTrade.RequestedQuantity
            : executedQuantity;

        trackedTrade.Symbol = string.IsNullOrWhiteSpace(venueTrade.Symbol) ? trackedTrade.Symbol : venueTrade.Symbol;
        trackedTrade.EntryPrice = venueTrade.EntryPrice > 0m ? venueTrade.EntryPrice : trackedTrade.EntryPrice;
        trackedTrade.MarkPrice = trackedTrade.EntryPrice;
        trackedTrade.EntryTime = venueTrade.EntryTime == default ? trackedTrade.EntryTime : venueTrade.EntryTime;
        trackedTrade.ExecutionLatencyMs = venueTrade.ExecutionLatencyMs > 0 ? venueTrade.ExecutionLatencyMs : trackedTrade.ExecutionLatencyMs;
        trackedTrade.EntrySlippagePercent = venueTrade.EntrySlippagePercent != 0m ? venueTrade.EntrySlippagePercent : trackedTrade.EntrySlippagePercent;
        trackedTrade.ExecutionMode = string.IsNullOrWhiteSpace(venueTrade.ExecutionMode) ? trackedTrade.ExecutionMode : venueTrade.ExecutionMode;
        trackedTrade.PromotionStage = string.IsNullOrWhiteSpace(venueTrade.PromotionStage) ? trackedTrade.PromotionStage : venueTrade.PromotionStage;
        trackedTrade.Quantity = executedQuantity;
        trackedTrade.FilledQuantity = executedQuantity;
        trackedTrade.RemainingQuantity = executedQuantity;
        trackedTrade.ClosedQuantity = 0m;
        trackedTrade.RequestedQuantity = requestedQuantity;
        trackedTrade.Status = TradeStatus.Executed;
        trackedTrade.LifecycleStatus = executedQuantity + 0.00000001m < requestedQuantity
            ? TradeLifecycleStatus.PartiallyFilled
            : TradeLifecycleStatus.Active;
        trackedTrade.LastUpdatedTime = now;
        trackedTrade.OptimalPriceSinceEntry = trackedTrade.EntryPrice;
        trackedTrade.ExitReason = string.Empty;
    }

    public void MarkFailed(Trade trade, string reason)
    {
        trade.Status = TradeStatus.Failed;
        trade.LifecycleStatus = TradeLifecycleStatus.Failed;
        trade.ExitReason = reason;
        trade.LastUpdatedTime = DateTime.UtcNow;
    }

    public PositionLifecycleAction? AdvancePaperPosition(PaperPosition position, Candle current, Candle previous, decimal atr)
    {
        position.BarsHeld++;
        position.CurrentPrice = current.Close;
        ApplyBreakevenProtection(position, current);

        var partialAction = TryResolvePartialExit(position, current.Close);
        if (partialAction != null)
            return partialAction;

        bool trailingHit = TrailingStopService.UpdateTrailingStop(position, current.High, current.Low, (double)atr);
        var exitReason = ResolveExitReason(position, current, previous, trailingHit);
        if (exitReason != null)
        {
            return new PositionLifecycleAction(
                PositionLifecycleActionType.FullExit,
                exitReason,
                Math.Max(0m, position.Quantity),
                current.Close);
        }

        return null;
    }

    public PositionLifecycleAction? AdvanceTrade(Trade trade, Candle current, Candle previous, decimal atr)
    {
        trade.BarsHeld++;
        trade.MarkPrice = current.Close;
        ApplyBreakevenProtection(trade, current);

        var partialAction = TryResolvePartialExit(trade, current.Close);
        if (partialAction != null)
            return partialAction;

        bool trailingHit = TrailingStopService.UpdateTrailingStop(trade, current.High, current.Low, (double)atr);
        var exitReason = ResolveExitReason(trade, current, previous, trailingHit);
        if (exitReason != null)
        {
            return new PositionLifecycleAction(
                PositionLifecycleActionType.FullExit,
                exitReason,
                Math.Max(0m, trade.Quantity),
                current.Close);
        }

        trade.LastUpdatedTime = DateTime.UtcNow;
        return null;
    }

    public void ApplyExitFill(Trade trade, decimal exitPrice, decimal exitQuantity, string reason, DateTime exitTimeUtc)
    {
        if (exitQuantity <= 0m)
            return;

        var boundedExitQuantity = Math.Min(exitQuantity, Math.Max(0m, trade.Quantity));
        var totalClosed = trade.ClosedQuantity + boundedExitQuantity;

        trade.AverageExitPrice = totalClosed <= 0m
            ? 0m
            : ((trade.AverageExitPrice * trade.ClosedQuantity) + (exitPrice * boundedExitQuantity)) / totalClosed;
        trade.ClosedQuantity = totalClosed;
        trade.ExitPrice = exitPrice;
        trade.Quantity = Math.Max(0m, trade.Quantity - boundedExitQuantity);
        trade.RemainingQuantity = trade.Quantity;
        trade.MarkPrice = exitPrice;
        trade.ExitReason = reason;
        trade.LastUpdatedTime = exitTimeUtc;
        if (string.Equals(reason, "PartialTakeProfit", StringComparison.OrdinalIgnoreCase))
            trade.HasTakenPartialProfit = true;

        if (trade.Quantity <= 0m)
        {
            trade.ExitTime = exitTimeUtc;
            trade.LifecycleStatus = TradeLifecycleStatus.Closed;
        }
        else
        {
            trade.LifecycleStatus = TradeLifecycleStatus.PartialExit;
        }
    }

    private static PositionLifecycleAction? TryResolvePartialExit(PaperPosition position, decimal currentPrice)
    {
        if (position.HasTakenPartialProfit || position.PartialTakeProfit <= 0m || position.Quantity <= 0m)
            return null;

        var partialTriggered = position.Side == TradeSide.Buy
            ? currentPrice >= position.PartialTakeProfit
            : currentPrice <= position.PartialTakeProfit;
        if (!partialTriggered)
            return null;

        var quantityToClose = position.PartialExitFraction > 0m
            ? Math.Min(position.Quantity, position.Quantity * position.PartialExitFraction)
            : position.Quantity * 0.50m;

        return quantityToClose <= 0m
            ? null
            : new PositionLifecycleAction(
                PositionLifecycleActionType.PartialExit,
                "PartialTakeProfit",
                quantityToClose,
                currentPrice);
    }

    private static PositionLifecycleAction? TryResolvePartialExit(Trade trade, decimal currentPrice)
    {
        if (trade.HasTakenPartialProfit || trade.PartialTakeProfit <= 0m || trade.Quantity <= 0m)
            return null;

        var partialTriggered = trade.Side == TradeSide.Buy
            ? currentPrice >= trade.PartialTakeProfit
            : currentPrice <= trade.PartialTakeProfit;
        if (!partialTriggered)
            return null;

        var quantityToClose = trade.PartialExitFraction > 0m
            ? Math.Min(trade.Quantity, trade.Quantity * trade.PartialExitFraction)
            : trade.Quantity * 0.50m;

        return quantityToClose <= 0m
            ? null
            : new PositionLifecycleAction(
                PositionLifecycleActionType.PartialExit,
                "PartialTakeProfit",
                quantityToClose,
                currentPrice);
    }

    private static void ApplyBreakevenProtection(PaperPosition position, Candle candle)
    {
        if (position.IsBreakevenArmed || position.BreakevenTriggerFraction <= 0m)
            return;

        var targetDistance = Math.Abs(position.TakeProfit - position.EntryPrice);
        if (targetDistance <= 0m)
            return;

        var triggerDistance = targetDistance * position.BreakevenTriggerFraction;
        var triggerReached = position.Side == TradeSide.Buy
            ? candle.High >= position.EntryPrice + triggerDistance
            : candle.Low <= position.EntryPrice - triggerDistance;
        if (!triggerReached)
            return;

        position.IsBreakevenArmed = true;
        var buffer = position.EntryPrice * 0.0005m;
        position.StopLoss = position.Side == TradeSide.Buy
            ? Math.Max(position.StopLoss, position.EntryPrice + buffer)
            : position.StopLoss == 0m
                ? position.EntryPrice - buffer
                : Math.Min(position.StopLoss, position.EntryPrice - buffer);
    }

    private static void ApplyBreakevenProtection(Trade trade, Candle candle)
    {
        if (trade.IsBreakevenArmed || trade.BreakevenTriggerFraction <= 0m)
            return;

        var targetDistance = Math.Abs(trade.TakeProfit - trade.EntryPrice);
        if (targetDistance <= 0m)
            return;

        var triggerDistance = targetDistance * trade.BreakevenTriggerFraction;
        var triggerReached = trade.Side == TradeSide.Buy
            ? candle.High >= trade.EntryPrice + triggerDistance
            : candle.Low <= trade.EntryPrice - triggerDistance;
        if (!triggerReached)
            return;

        trade.IsBreakevenArmed = true;
        var buffer = trade.EntryPrice * 0.0005m;
        trade.StopLoss = trade.Side == TradeSide.Buy
            ? Math.Max(trade.StopLoss, trade.EntryPrice + buffer)
            : trade.StopLoss == 0m
                ? trade.EntryPrice - buffer
                : Math.Min(trade.StopLoss, trade.EntryPrice - buffer);
    }

    private static string? ResolveExitReason(PaperPosition position, Candle current, Candle previous, bool trailingHit)
    {
        if (trailingHit) return "TrailingSL";
        if (position.Side == TradeSide.Buy)
        {
            if (position.StopLoss > 0m && current.Close <= position.StopLoss) return "StopLoss";
            if (position.TakeProfit > 0m && current.Close >= position.TakeProfit) return "TakeProfit";
        }
        else
        {
            if (position.StopLoss > 0m && current.Close >= position.StopLoss) return "StopLoss";
            if (position.TakeProfit > 0m && current.Close <= position.TakeProfit) return "TakeProfit";
        }

        var strategyExit = TryGetStrategyPolicyExit(position.ExitPolicyTag, position.Side, position.IsBreakevenArmed, position.BarsHeld, position.MaxHoldingBars, current, previous);
        if (strategyExit != null) return strategyExit;

        if (position.MaxHoldingBars > 0 && position.BarsHeld >= position.MaxHoldingBars)
            return $"TimeStop({position.ExitPolicyTag})";

        return null;
    }

    private static string? ResolveExitReason(Trade trade, Candle current, Candle previous, bool trailingHit)
    {
        if (trailingHit) return "TrailingSL";
        if (trade.Side == TradeSide.Buy)
        {
            if (trade.StopLoss > 0m && current.Close <= trade.StopLoss) return "StopLoss";
            if (trade.TakeProfit > 0m && current.Close >= trade.TakeProfit) return "TakeProfit";
        }
        else
        {
            if (trade.StopLoss > 0m && current.Close >= trade.StopLoss) return "StopLoss";
            if (trade.TakeProfit > 0m && current.Close <= trade.TakeProfit) return "TakeProfit";
        }

        var strategyExit = TryGetStrategyPolicyExit(trade.ExitPolicyTag, trade.Side, trade.IsBreakevenArmed, trade.BarsHeld, trade.MaxHoldingBars, current, previous);
        if (strategyExit != null) return strategyExit;

        if (trade.MaxHoldingBars > 0 && trade.BarsHeld >= trade.MaxHoldingBars)
            return $"TimeStop({trade.ExitPolicyTag})";

        return null;
    }

    private static string? TryGetStrategyPolicyExit(
        string exitPolicyTag,
        TradeSide side,
        bool isBreakevenArmed,
        int barsHeld,
        int maxHoldingBars,
        Candle candle,
        Candle previous)
    {
        var bearishBar = candle.Close < candle.Open && candle.Close < previous.Close;
        var bullishBar = candle.Close > candle.Open && candle.Close > previous.Close;

        return exitPolicyTag switch
        {
            "TrendRunner" when isBreakevenArmed &&
                                barsHeld >= Math.Max(6, maxHoldingBars / 2) &&
                                ((side == TradeSide.Buy && bearishBar) || (side == TradeSide.Sell && bullishBar))
                => "TrendMomentumFade",
            "MeanReversion" when barsHeld >= 4 && !isBreakevenArmed
                => "MeanReversionTimeout",
            "BreakoutExpansion" when barsHeld >= 3 && isBreakevenArmed &&
                                       ((side == TradeSide.Buy && bearishBar) || (side == TradeSide.Sell && bullishBar))
                => "BreakoutFailure",
            "HftScalp" when barsHeld >= 2
                => "HftMicroExit",
            _ => null
        };
    }

    private static decimal TryGetDecimal(Signal signal, string key, decimal fallback = 0m)
        => signal.Metadata.TryGetValue(key, out var value) &&
           decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static int TryGetInt(Signal signal, string key, int fallback = 0)
        => signal.Metadata.TryGetValue(key, out var value) &&
           int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static string? TryGetString(Signal signal, string key)
        => signal.Metadata.TryGetValue(key, out var value) ? value : null;
}
