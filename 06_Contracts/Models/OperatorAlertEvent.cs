namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Cross-process inter-service alert. Any service in any process can write a row;
/// the Listener.Api OperatorAlertDbReaderWorker polls new rows and republishes them
/// via MediatR + SignalR so the dashboard NotificationsBell shows them.
///
/// Distinct from <see cref="AlertHistoryEntry"/>, which tracks Telegram dispatch outcomes.
/// </summary>
public sealed class OperatorAlertEvent
{
    public long Id { get; set; }
    public DateTime UtcTime { get; set; } = DateTime.UtcNow;
    public string Severity { get; set; } = "info"; // info | success | warning | error
    public string Source { get; set; } = "Unknown"; // RiskSupervisor, Promotion, Funding, Pairs, ...
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
}
