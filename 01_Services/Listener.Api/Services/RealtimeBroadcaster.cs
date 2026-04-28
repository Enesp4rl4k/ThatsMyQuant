using MediatR;
using Microsoft.AspNetCore.SignalR;
using Listener.Api.Hubs;
using ThatsMyQuant.Application.Strategy.ProMode;
using ThatsMyQuant.Contracts.Notifications;

namespace Listener.Api.Services;

/// <summary>
/// Bridges MediatR notifications onto the SignalR hub so the web dashboard receives
/// position-close and circuit-breaker events in real time without polling.
/// </summary>
public sealed class RealtimeBroadcaster :
    INotificationHandler<PositionOpenedNotification>,
    INotificationHandler<PositionClosedNotification>,
    INotificationHandler<CircuitBreakerTrippedNotification>,
    INotificationHandler<OperatorAlertNotification>
{
    private readonly IHubContext<MarketDataHub> _hub;
    private readonly ILogger<RealtimeBroadcaster> _logger;

    public RealtimeBroadcaster(IHubContext<MarketDataHub> hub, ILogger<RealtimeBroadcaster> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Handle(OperatorAlertNotification n, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("OperatorAlert", new
            {
                severity = n.Severity,
                source = n.Source,
                title = n.Title,
                body = n.Body,
                utcTime = n.UtcTime.ToString("O")
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RealtimeBroadcaster: OperatorAlert broadcast failed.");
        }
    }

    public async Task Handle(PositionOpenedNotification n, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("PositionOpened", new
            {
                symbol         = n.Symbol,
                strategyTag    = n.StrategyTag,
                regimeTag      = n.RegimeTag,
                exitPolicyTag  = n.ExitPolicyTag,
                executionMode  = n.ExecutionMode,
                side           = n.Side.ToString(),
                entryPrice     = n.EntryPrice,
                quantity       = n.Quantity,
                stopLoss       = n.StopLoss,
                takeProfit     = n.TakeProfit,
                edgeScore      = n.EdgeScore,
                confidence     = n.Confidence,
                reasoning      = n.Reasoning,
                entryTimeUtc   = n.EntryTimeUtc.ToString("O"),
                correlationId  = n.CorrelationId,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RealtimeBroadcaster: PositionOpened broadcast failed.");
        }
    }

    public async Task Handle(PositionClosedNotification n, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("PositionClosed", new
            {
                symbol = n.Symbol,
                strategyTag = n.StrategyTag,
                exitReason = n.ExitReason,
                executionMode = n.ExecutionMode,
                side = n.Side.ToString(),
                netPnl = n.NetPnl,
                netPnlPercent = n.NetPnlPercent,
                exitTimeUtc = n.ExitTimeUtc.ToString("O")
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RealtimeBroadcaster: PositionClosed broadcast failed.");
        }
    }

    public async Task Handle(CircuitBreakerTrippedNotification n, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("CircuitBreakerTripped", new
            {
                reason = n.Reason,
                realizedDrawdown = n.RealizedDrawdown,
                peakEquity = n.PeakEquity,
                currentEquity = n.CurrentEquity,
                trippedUtc = n.TrippedUtc.ToString("O")
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RealtimeBroadcaster: CircuitBreakerTripped broadcast failed.");
        }
    }
}
