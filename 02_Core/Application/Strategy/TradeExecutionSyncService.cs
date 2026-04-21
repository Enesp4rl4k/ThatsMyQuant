using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public sealed record StartupExecutionReconciliationResult(
    int MatchedTrades,
    int UpdatedTrades,
    int OrphanedVenueOrders,
    int MissingLocalOrders);

public sealed class TradeExecutionSyncService
{
    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<TradeExecutionSyncService> _logger;

    public TradeExecutionSyncService(
        ITradeRepository tradeRepository,
        ILogger<TradeExecutionSyncService> logger)
    {
        _tradeRepository = tradeRepository;
        _logger = logger;
    }

    public async Task<StartupExecutionReconciliationResult> ReconcileOpenOrdersAsync(
        IReadOnlyList<OpenOrderSnapshot> openOrders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openOrders);

        var openTrades = await _tradeRepository.GetOpenAsync(Math.Max(128, openOrders.Count * 4));
        var tradesBySignalId = openTrades.ToDictionary(trade => trade.SignalId);
        var seenSignalIds = new HashSet<Guid>();

        var matchedTrades = 0;
        var updatedTrades = 0;
        var orphanedVenueOrders = 0;

        foreach (var order in openOrders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ExecutionOrderIdentity.TryParseSignalId(order.ClientOrderId, out var signalId) ||
                !tradesBySignalId.TryGetValue(signalId, out var trade))
            {
                orphanedVenueOrders++;
                _logger.LogWarning(
                    "Startup reconciliation found orphaned venue order {ClientOrderId} ({Symbol}, {Status}) with no local trade.",
                    order.ClientOrderId,
                    order.Symbol,
                    order.Status);
                continue;
            }

            matchedTrades++;
            seenSignalIds.Add(signalId);

            if (!ApplyVenueState(trade, order.Status, order.FilledQuantity, order.RemainingQuantity, order.AveragePrice, order.Price, order.TimestampUtc))
            {
                continue;
            }

            await _tradeRepository.UpdateAsync(trade);
            updatedTrades++;
        }

        var missingLocalOrders = 0;
        foreach (var trade in openTrades.Where(RequiresVenuePresence).Where(trade => !seenSignalIds.Contains(trade.SignalId)))
        {
            missingLocalOrders++;
            _logger.LogWarning(
                "Startup reconciliation could not find an open venue order for local Trade {TradeId} / Signal {SignalId} ({Symbol}, {LifecycleStatus}).",
                trade.Id,
                trade.SignalId,
                trade.Symbol,
                trade.LifecycleStatus);
        }

        return new StartupExecutionReconciliationResult(
            matchedTrades,
            updatedTrades,
            orphanedVenueOrders,
            missingLocalOrders);
    }

    public async Task<bool> ApplyExecutionReportAsync(ExecutionReport report, CancellationToken cancellationToken = default)
    {
        var result = await ApplyExecutionReportDetailedAsync(report, cancellationToken);
        return result.Applied;
    }

    public sealed record ExecutionReportApplyResult(
        bool Applied,
        Guid? SignalId,
        Trade? Trade,
        decimal PreviousFilledQuantity,
        decimal NewFilledQuantity,
        decimal DeltaFilledQuantity);

    public async Task<ExecutionReportApplyResult> ApplyExecutionReportDetailedAsync(
        ExecutionReport report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ExecutionOrderIdentity.TryParseSignalId(report.ClientOrderId, out var signalId))
        {
            _logger.LogWarning(
                "Execution report for {Symbol} ignored because client order id '{ClientOrderId}' is not a signal-backed id.",
                report.Symbol,
                report.ClientOrderId);
            return new ExecutionReportApplyResult(false, null, null, 0m, 0m, 0m);
        }

        var trade = await _tradeRepository.GetBySignalIdAsync(signalId);
        if (trade == null)
        {
            _logger.LogWarning(
                "Execution report for {Symbol} ignored because no local trade exists for Signal {SignalId}.",
                report.Symbol,
                signalId);
            return new ExecutionReportApplyResult(false, signalId, null, 0m, 0m, 0m);
        }

        var previousFilled = trade.FilledQuantity;

        var changed = ApplyVenueState(
            trade,
            report.Status,
            report.FilledQuantity,
            report.RemainingQuantity,
            report.AveragePrice,
            report.LastFillPrice,
            report.TimestampUtc,
            report.LatencyMs);

        if (!changed)
        {
            return new ExecutionReportApplyResult(false, signalId, trade, previousFilled, trade.FilledQuantity, 0m);
        }

        await _tradeRepository.UpdateAsync(trade);
        var newFilled = trade.FilledQuantity;
        var deltaFilled = Math.Max(0m, newFilled - previousFilled);
        return new ExecutionReportApplyResult(true, signalId, trade, previousFilled, newFilled, deltaFilled);
    }

    private static bool RequiresVenuePresence(Trade trade)
        => trade.LifecycleStatus is TradeLifecycleStatus.Submitted or TradeLifecycleStatus.Accepted or TradeLifecycleStatus.PartiallyFilled;

    private static bool ApplyVenueState(
        Trade trade,
        string venueStatus,
        decimal venueFilledQuantity,
        decimal venueRemainingQuantity,
        decimal averagePrice,
        decimal fallbackPrice,
        DateTime timestampUtc,
        long? latencyMs = null)
    {
        var filledQuantity = Math.Max(trade.FilledQuantity, venueFilledQuantity);
        var requestedQuantity = trade.RequestedQuantity > 0m
            ? trade.RequestedQuantity
            : Math.Max(0m, venueFilledQuantity + venueRemainingQuantity);
        var remainingQuantity = venueRemainingQuantity > 0m
            ? venueRemainingQuantity
            : Math.Max(0m, requestedQuantity - filledQuantity);
        var lifecycleStatus = ResolveLifecycleStatus(venueStatus, trade.Side, filledQuantity, remainingQuantity);
        var status = ResolveTradeStatus(venueStatus, filledQuantity);
        var resolvedPrice = averagePrice > 0m
            ? averagePrice
            : (fallbackPrice > 0m ? fallbackPrice : trade.EntryPrice);
        var resolvedEntryPrice = trade.EntryPrice > 0m ? trade.EntryPrice : resolvedPrice;
        var resolvedTimestamp = timestampUtc == default ? DateTime.UtcNow : timestampUtc;
        var submittedTime = trade.SubmittedTime ?? resolvedTimestamp;
        var exitTime = trade.ExitTime;
        var exitPrice = trade.ExitPrice;
        var averageExitPrice = trade.AverageExitPrice;
        var closedQuantity = trade.ClosedQuantity;
        var exitReason = trade.ExitReason;
        var executionLatencyMs = latencyMs.HasValue
            ? Math.Max(0L, latencyMs.Value)
            : Math.Max(0L, trade.ExecutionLatencyMs);

        if (lifecycleStatus == TradeLifecycleStatus.Closed)
        {
            exitTime ??= resolvedTimestamp;
            exitPrice = resolvedPrice;
            averageExitPrice = resolvedPrice;
            closedQuantity = Math.Max(closedQuantity, filledQuantity);
            if (string.IsNullOrWhiteSpace(exitReason))
            {
                exitReason = "ExecutionReportFilled";
            }
        }
        else if (lifecycleStatus is TradeLifecycleStatus.Cancelled or TradeLifecycleStatus.Rejected or TradeLifecycleStatus.Failed)
        {
            exitTime ??= resolvedTimestamp;
            if (exitPrice <= 0m)
            {
                exitPrice = resolvedPrice;
            }
        }

        var changed =
            trade.EntryPrice != resolvedEntryPrice ||
            trade.MarkPrice != resolvedPrice ||
            trade.Quantity != filledQuantity ||
            trade.FilledQuantity != filledQuantity ||
            trade.RemainingQuantity != remainingQuantity ||
            trade.Status != status ||
            trade.LifecycleStatus != lifecycleStatus ||
            trade.SubmittedTime != submittedTime ||
            trade.LastUpdatedTime != resolvedTimestamp ||
            trade.ExecutionLatencyMs != executionLatencyMs ||
            trade.ExitTime != exitTime ||
            trade.ExitPrice != exitPrice ||
            trade.AverageExitPrice != averageExitPrice ||
            trade.ClosedQuantity != closedQuantity ||
            !string.Equals(trade.ExitReason, exitReason, StringComparison.Ordinal);

        trade.EntryPrice = resolvedEntryPrice;
        trade.MarkPrice = resolvedPrice;
        trade.Quantity = filledQuantity;
        trade.FilledQuantity = filledQuantity;
        trade.RequestedQuantity = requestedQuantity;
        trade.RemainingQuantity = remainingQuantity;
        trade.Status = status;
        trade.LifecycleStatus = lifecycleStatus;
        trade.SubmittedTime = submittedTime;
        trade.LastUpdatedTime = resolvedTimestamp;
        trade.ExitTime = exitTime;
        trade.ExitPrice = exitPrice;
        trade.AverageExitPrice = averageExitPrice;
        trade.ClosedQuantity = closedQuantity;
        trade.ExitReason = exitReason;
        trade.ExecutionLatencyMs = executionLatencyMs;

        return changed;
    }

    private static TradeLifecycleStatus ResolveLifecycleStatus(string status, TradeSide side, decimal filledQuantity, decimal remainingQuantity)
    {
        switch (status.Trim().ToUpperInvariant())
        {
            case "FILLED":
                // Spot sell fills usually represent exit/deallocation, while buy fills open inventory.
                return side == TradeSide.Sell ? TradeLifecycleStatus.Closed : TradeLifecycleStatus.Active;

            case "PARTIALLY_FILLED":
            case "PARTIALLYFILLED":
            case "PARTIAL_FILL":
            case "LIVE":
            case "OPEN":
                return filledQuantity > 0m && remainingQuantity > 0m
                    ? TradeLifecycleStatus.PartiallyFilled
                    : TradeLifecycleStatus.Accepted;

            case "NEW":
            case "PENDING":
            case "ACCEPTED":
                return filledQuantity > 0m && remainingQuantity > 0m
                    ? TradeLifecycleStatus.PartiallyFilled
                    : TradeLifecycleStatus.Accepted;

            case "CANCELED":
            case "CANCELLED":
                return filledQuantity > 0m
                    ? TradeLifecycleStatus.PartiallyFilled
                    : TradeLifecycleStatus.Cancelled;

            case "REJECTED":
                return TradeLifecycleStatus.Rejected;

            case "FAILED":
            case "EXPIRED":
                return TradeLifecycleStatus.Failed;

            default:
                if (filledQuantity > 0m && remainingQuantity > 0m)
                {
                    return TradeLifecycleStatus.PartiallyFilled;
                }

                return filledQuantity > 0m
                    ? TradeLifecycleStatus.Active
                    : TradeLifecycleStatus.Accepted;
        }
    }

    private static TradeStatus ResolveTradeStatus(string status, decimal filledQuantity)
    {
        if (filledQuantity > 0m)
        {
            return TradeStatus.Executed;
        }

        return status.Trim().ToUpperInvariant() switch
        {
            "REJECTED" => TradeStatus.Rejected,
            "FAILED" or "EXPIRED" or "CANCELED" or "CANCELLED" => TradeStatus.Failed,
            _ => TradeStatus.Pending
        };
    }
}
