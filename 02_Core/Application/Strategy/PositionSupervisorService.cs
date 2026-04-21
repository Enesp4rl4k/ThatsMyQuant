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
            Leverage = TryGetDecimal(signal, "Leverage", 1m),
            MarginMode = Enum.TryParse<MarginMode>(TryGetString(signal, "MarginMode"), true, out var marginMode)
                ? marginMode
                : MarginMode.Isolated,
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

    public void MarkClosing(Trade trade, string reason, DateTime? atUtc = null)
    {
        trade.LifecycleStatus = TradeLifecycleStatus.Closing;
        trade.ExitReason = reason;
        trade.LastUpdatedTime = atUtc ?? DateTime.UtcNow;
    }

    public void ApplyExecutionFill(Trade trackedTrade, Trade venueTrade)
    {
        var now = DateTime.UtcNow;
        var executedQuantity = venueTrade.FilledQuantity > 0m
            ? venueTrade.FilledQuantity
            : venueTrade.Quantity;
        var requestedQuantity = trackedTrade.RequestedQuantity > 0m
            ? trackedTrade.RequestedQuantity
            : (venueTrade.RequestedQuantity > 0m ? venueTrade.RequestedQuantity : executedQuantity);

        trackedTrade.Symbol = string.IsNullOrWhiteSpace(venueTrade.Symbol) ? trackedTrade.Symbol : venueTrade.Symbol;
        trackedTrade.EntryPrice = venueTrade.EntryPrice > 0m ? venueTrade.EntryPrice : trackedTrade.EntryPrice;
        trackedTrade.MarkPrice = venueTrade.MarkPrice > 0m
            ? venueTrade.MarkPrice
            : (trackedTrade.EntryPrice > 0m ? trackedTrade.EntryPrice : trackedTrade.MarkPrice);
        trackedTrade.EntryTime = venueTrade.EntryTime == default
            ? trackedTrade.EntryTime
            : venueTrade.EntryTime;
        trackedTrade.ExecutionLatencyMs = venueTrade.ExecutionLatencyMs > 0 ? venueTrade.ExecutionLatencyMs : trackedTrade.ExecutionLatencyMs;
        trackedTrade.EntrySlippagePercent = venueTrade.EntrySlippagePercent != 0m ? venueTrade.EntrySlippagePercent : trackedTrade.EntrySlippagePercent;
        trackedTrade.ExecutionMode = string.IsNullOrWhiteSpace(venueTrade.ExecutionMode) ? trackedTrade.ExecutionMode : venueTrade.ExecutionMode;
        trackedTrade.PromotionStage = string.IsNullOrWhiteSpace(venueTrade.PromotionStage) ? trackedTrade.PromotionStage : venueTrade.PromotionStage;
        trackedTrade.RequestedQuantity = requestedQuantity;
        trackedTrade.Quantity = executedQuantity;
        trackedTrade.FilledQuantity = executedQuantity;
        trackedTrade.RemainingQuantity = ResolveRemainingQuantity(trackedTrade, venueTrade, requestedQuantity, executedQuantity);
        trackedTrade.ClosedQuantity = 0m;
        trackedTrade.AverageExitPrice = venueTrade.AverageExitPrice > 0m ? venueTrade.AverageExitPrice : trackedTrade.AverageExitPrice;
        trackedTrade.SubmittedTime ??= venueTrade.SubmittedTime ?? now;
        trackedTrade.Status = ResolveExecutionStatus(venueTrade, executedQuantity);
        trackedTrade.LifecycleStatus = ResolveExecutionLifecycle(venueTrade, requestedQuantity, executedQuantity);
        trackedTrade.LastUpdatedTime = now;
        trackedTrade.OptimalPriceSinceEntry = trackedTrade.EntryPrice;
        trackedTrade.ExitReason = trackedTrade.Status == TradeStatus.Failed
            ? venueTrade.ExitReason
            : string.Empty;
        trackedTrade.TotalFeesPaid += venueTrade.TotalFeesPaid;
        trackedTrade.NetFunding += venueTrade.NetFunding;
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
        UpdateMafe(position, current);
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
        UpdateMafe(trade, current);
        ApplyExecutionStressOverlay(trade);
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

        if (trailingHit) return "TrailingSL";
        if (position.MaxAdverseExcursionPercent <= -3.0m) return "MafeStatisticalStop";

        var strategyExit = TryGetStrategyPolicyExit(position.ExitPolicyTag, position.Side, position.IsBreakevenArmed, position.BarsHeld, position.MaxHoldingBars, current, previous);
        if (strategyExit != null) return strategyExit;

        if (position.MaxHoldingBars > 0 && position.BarsHeld >= position.MaxHoldingBars)
            return $"TimeStop({position.ExitPolicyTag})";

        return null;
    }

    private static string? ResolveExitReason(Trade trade, Candle current, Candle previous, bool trailingHit)
    {
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

        if (trailingHit) return "TrailingSL";
        if (trade.MaxAdverseExcursionPercent <= -3.0m) return "MafeStatisticalStop";

        var strategyExit = TryGetStrategyPolicyExit(
            trade.ExitPolicyTag,
            trade.Side,
            trade.IsBreakevenArmed,
            trade.BarsHeld,
            trade.MaxHoldingBars,
            current,
            previous,
            ResolveExecutionStress(trade));
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
        Candle previous,
        decimal executionStress = 0m)
    {
        var bearishBar =
            (candle.Close < candle.Open || candle.Close < previous.Close || candle.Low < previous.Low) &&
            candle.Close <= previous.Close;
        var bullishBar =
            (candle.Close > candle.Open || candle.Close > previous.Close || candle.High > previous.High) &&
            candle.Close >= previous.Close;

        // Breakeven armed check removed — BE stops disabled across routers to protect R:R.
        // Strategy-policy exits now gated on bars-held ratio against MaxHoldingBars so they
        // don't fire before TP has a realistic chance to hit.
        var stressCompression = 1m - Math.Clamp(executionStress * 0.35m, 0m, 0.45m);
        var trendFadeBars = maxHoldingBars > 0 ? Math.Max(6, (int)(maxHoldingBars * 0.65m * stressCompression)) : 12;
        var meanRevBars = maxHoldingBars > 0 ? Math.Max(6, (int)(maxHoldingBars * 0.75m * stressCompression)) : 8;
        var breakoutFailBars = maxHoldingBars > 0 ? Math.Max(4, (int)(maxHoldingBars * 0.50m * stressCompression)) : 6;
        var momentumAgainstPosition = side == TradeSide.Buy
            ? candle.Close < previous.Close
            : candle.Close > previous.Close;
        var hasOpposingBar = (side == TradeSide.Buy && bearishBar) || (side == TradeSide.Sell && bullishBar) || momentumAgainstPosition;

        if (executionStress >= 0.70m && barsHeld >= 2 && hasOpposingBar)
            return "ExecutionStressMomentumFade";

        return exitPolicyTag switch
        {
            "TrendRunner" when barsHeld >= trendFadeBars &&
                                hasOpposingBar
                => "TrendMomentumFade",
            "MeanReversion" when barsHeld >= meanRevBars
                => "MeanReversionTimeout",
            "BreakoutExpansion" when barsHeld >= breakoutFailBars &&
                                       hasOpposingBar
                => "BreakoutFailure",
            "HftScalp" when barsHeld >= 4
                => "HftMicroExit",
            _ => null
        };
    }

    private static void ApplyExecutionStressOverlay(Trade trade)
    {
        var executionStress = ResolveExecutionStress(trade);
        if (executionStress <= 0m)
            return;

        if (!trade.IsBreakevenArmed && trade.BreakevenTriggerFraction > 0m)
        {
            var stressedBreakeven = Math.Max(0.12m, trade.BreakevenTriggerFraction * (1m - (0.45m * executionStress)));
            trade.BreakevenTriggerFraction = Math.Min(trade.BreakevenTriggerFraction, stressedBreakeven);
        }

        if (trade.MaxHoldingBars > 0)
        {
            var stressedMaxBars = Math.Max(3, (int)Math.Round(trade.MaxHoldingBars * (1m - (0.35m * executionStress))));
            trade.MaxHoldingBars = Math.Min(trade.MaxHoldingBars, stressedMaxBars);
        }

        if (!trade.HasTakenPartialProfit && trade.PartialTakeProfit > 0m && trade.EntryPrice > 0m)
        {
            var currentDistance = Math.Abs(trade.PartialTakeProfit - trade.EntryPrice);
            var stressedDistance = currentDistance * (1m - (0.20m * executionStress));
            if (stressedDistance < currentDistance)
            {
                trade.PartialTakeProfit = trade.Side == TradeSide.Buy
                    ? trade.EntryPrice + stressedDistance
                    : trade.EntryPrice - stressedDistance;
            }
        }
    }

    private static decimal ResolveExecutionStress(Trade trade)
    {
        decimal stress = 0m;

        if (trade.EntrySlippagePercent > 0.05m)
        {
            stress += Math.Clamp((trade.EntrySlippagePercent - 0.05m) / 0.20m, 0m, 0.60m);
        }

        if (trade.ExecutionLatencyMs > 150)
        {
            stress += Math.Clamp((trade.ExecutionLatencyMs - 150m) / 900m, 0m, 0.50m);
        }

        if (trade.VenueExecutionBias > 0m && trade.VenueExecutionBias < 1.0m)
        {
            stress += Math.Clamp((1.0m - trade.VenueExecutionBias) / 0.20m, 0m, 0.50m);
        }

        if (!string.IsNullOrWhiteSpace(trade.VenueExecutionScope) &&
            trade.VenueExecutionScope.Contains("degrad", StringComparison.OrdinalIgnoreCase))
        {
            stress += 0.15m;
        }

        return Math.Clamp(stress, 0m, 1m);
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

    private static TradeStatus ResolveExecutionStatus(Trade venueTrade, decimal executedQuantity)
    {
        if (venueTrade.Status == TradeStatus.Failed ||
            venueTrade.LifecycleStatus is TradeLifecycleStatus.Rejected or TradeLifecycleStatus.Failed)
        {
            return TradeStatus.Failed;
        }

        if (executedQuantity > 0m)
        {
            return TradeStatus.Executed;
        }

        return venueTrade.Status == TradeStatus.Executed
            ? TradeStatus.Pending
            : venueTrade.Status;
    }

    private static TradeLifecycleStatus ResolveExecutionLifecycle(Trade venueTrade, decimal requestedQuantity, decimal executedQuantity)
    {
        if (venueTrade.LifecycleStatus is TradeLifecycleStatus.Rejected or TradeLifecycleStatus.Failed)
        {
            return venueTrade.LifecycleStatus;
        }

        if (venueTrade.LifecycleStatus is TradeLifecycleStatus.Submitted or TradeLifecycleStatus.Accepted)
        {
            return executedQuantity > 0m
                ? (executedQuantity + 0.00000001m < requestedQuantity
                    ? TradeLifecycleStatus.PartiallyFilled
                    : TradeLifecycleStatus.Active)
                : venueTrade.LifecycleStatus;
        }

        if (venueTrade.LifecycleStatus == TradeLifecycleStatus.PartiallyFilled)
        {
            return TradeLifecycleStatus.PartiallyFilled;
        }

        if (venueTrade.LifecycleStatus == TradeLifecycleStatus.Active)
        {
            return executedQuantity + 0.00000001m < requestedQuantity
                ? TradeLifecycleStatus.PartiallyFilled
                : TradeLifecycleStatus.Active;
        }

        if (executedQuantity <= 0m)
        {
            return TradeLifecycleStatus.Accepted;
        }

        return executedQuantity + 0.00000001m < requestedQuantity
            ? TradeLifecycleStatus.PartiallyFilled
            : TradeLifecycleStatus.Active;
    }

    private static decimal ResolveRemainingQuantity(Trade trackedTrade, Trade venueTrade, decimal requestedQuantity, decimal executedQuantity)
    {
        if (venueTrade.RemainingQuantity > 0m)
        {
            return venueTrade.RemainingQuantity;
        }

        if (venueTrade.LifecycleStatus is TradeLifecycleStatus.Submitted or TradeLifecycleStatus.Accepted)
        {
            return requestedQuantity > 0m
                ? Math.Max(0m, requestedQuantity - executedQuantity)
                : trackedTrade.RemainingQuantity;
        }

        if (requestedQuantity > 0m && executedQuantity + 0.00000001m < requestedQuantity)
        {
            return Math.Max(0m, requestedQuantity - executedQuantity);
        }

        return executedQuantity;
    }

    private static void UpdateMafe(Trade trade, Candle current)
    {
        if (trade.EntryPrice <= 0m) return;

        if (trade.OptimalPriceSinceEntry == 0m || (trade.Side == TradeSide.Buy ? current.High > trade.OptimalPriceSinceEntry : current.Low < trade.OptimalPriceSinceEntry))
            trade.OptimalPriceSinceEntry = trade.Side == TradeSide.Buy ? current.High : current.Low;

        if (trade.WorstPriceSinceEntry == 0m || (trade.Side == TradeSide.Buy ? current.Low < trade.WorstPriceSinceEntry : current.High > trade.WorstPriceSinceEntry))
            trade.WorstPriceSinceEntry = trade.Side == TradeSide.Buy ? current.Low : current.High;

        var direction = trade.Side == TradeSide.Buy ? 1m : -1m;
        var mfe = ((trade.OptimalPriceSinceEntry - trade.EntryPrice) / trade.EntryPrice) * direction * 100m;
        var mae = ((trade.WorstPriceSinceEntry - trade.EntryPrice) / trade.EntryPrice) * direction * 100m;

        if (mfe > trade.MaxFavorableExcursionPercent) trade.MaxFavorableExcursionPercent = mfe;
        if (mae < trade.MaxAdverseExcursionPercent) trade.MaxAdverseExcursionPercent = mae;
    }

    private static void UpdateMafe(PaperPosition position, Candle current)
    {
        if (position.EntryPrice <= 0m) return;

        if (position.OptimalPriceSinceEntry == 0m || (position.Side == TradeSide.Buy ? current.High > position.OptimalPriceSinceEntry : current.Low < position.OptimalPriceSinceEntry))
            position.OptimalPriceSinceEntry = position.Side == TradeSide.Buy ? current.High : current.Low;

        if (position.WorstPriceSinceEntry == 0m || (position.Side == TradeSide.Buy ? current.Low < position.WorstPriceSinceEntry : current.High > position.WorstPriceSinceEntry))
            position.WorstPriceSinceEntry = position.Side == TradeSide.Buy ? current.Low : current.High;

        var direction = position.Side == TradeSide.Buy ? 1m : -1m;
        var mfe = ((position.OptimalPriceSinceEntry - position.EntryPrice) / position.EntryPrice) * direction * 100m;
        var mae = ((position.WorstPriceSinceEntry - position.EntryPrice) / position.EntryPrice) * direction * 100m;

        if (mfe > position.MaxFavorableExcursionPercent) position.MaxFavorableExcursionPercent = mfe;
        if (mae < position.MaxAdverseExcursionPercent) position.MaxAdverseExcursionPercent = mae;
    }
}
