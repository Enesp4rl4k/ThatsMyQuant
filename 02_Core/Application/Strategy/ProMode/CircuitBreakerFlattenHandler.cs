using MediatR;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// MediatR subscriber that flattens the paper-trading portfolio whenever the
/// circuit breaker trips. Wired automatically via DI — no orchestrator code change
/// needed. This closes the "CB blocks new entries but existing positions keep
/// bleeding" gap.
///
/// Also fires an operator alert (Critical priority) so the user is notified
/// out-of-band even if they are not at the dashboard.
/// </summary>
public sealed class CircuitBreakerFlattenHandler : INotificationHandler<CircuitBreakerTrippedNotification>
{
    private readonly PaperTradingService? _paper;
    private readonly IOperatorAlertService _alerts;
    private readonly ILogger<CircuitBreakerFlattenHandler> _logger;

    public CircuitBreakerFlattenHandler(
        IOperatorAlertService alerts,
        ILogger<CircuitBreakerFlattenHandler> logger,
        PaperTradingService? paper = null)
    {
        _alerts = alerts;
        _logger = logger;
        _paper = paper;
    }

    public async Task Handle(CircuitBreakerTrippedNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogError(
            "Circuit breaker trip received — flattening. Reason: {Reason}. Peak {Peak:N2} → Current {Cur:N2} ({DD:P2} DD)",
            notification.Reason, notification.PeakEquity, notification.CurrentEquity, notification.RealizedDrawdown);

        var body = $"Portfolio DD {notification.RealizedDrawdown:P2}\n" +
                   $"Peak equity: {notification.PeakEquity:N2}\n" +
                   $"Current:     {notification.CurrentEquity:N2}\n" +
                   $"Reason:      {notification.Reason}\n" +
                   $"Trip time:   {notification.TrippedUtc:yyyy-MM-dd HH:mm:ss} UTC\n\n" +
                   "All new entries BLOCKED. Manual reset required after operator review.";

        if (_paper is not null)
        {
            try
            {
                var flattenSummary = await _paper.FlattenAllPositionsAsync(
                    reason: $"CircuitBreakerTrip({notification.RealizedDrawdown:P2})").ConfigureAwait(false);
                body += $"\n\n{flattenSummary}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Flatten-all failed during circuit breaker response");
                body += $"\n\n⚠️ FlattenAll FAILED: {ex.Message} — review positions manually.";
            }
        }

        await _alerts.NotifyAsync(
            AlertPriority.Critical,
            title: "🛑 CIRCUIT BREAKER TRIPPED",
            body: body,
            ct: cancellationToken).ConfigureAwait(false);
    }
}
