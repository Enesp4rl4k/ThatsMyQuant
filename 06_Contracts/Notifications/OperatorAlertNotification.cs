using MediatR;

namespace ThatsMyQuant.Contracts.Notifications;

/// <summary>
/// Generic operator-facing alert that fans out onto the dashboard NotificationsBell.
/// Any service in the process can publish one via MediatR; the Listener.Api
/// RealtimeBroadcaster will forward it to the SignalR `OperatorAlert` channel.
/// </summary>
public sealed record OperatorAlertNotification(
    string Severity,   // "info" | "success" | "warning" | "error"
    string Source,     // e.g. "RiskSupervisor", "Promotion", "Funding", "Settings"
    string Title,
    string Body,
    DateTime UtcTime) : INotification
{
    public static OperatorAlertNotification Info(string source, string title, string body)
        => new("info", source, title, body, DateTime.UtcNow);

    public static OperatorAlertNotification Success(string source, string title, string body)
        => new("success", source, title, body, DateTime.UtcNow);

    public static OperatorAlertNotification Warning(string source, string title, string body)
        => new("warning", source, title, body, DateTime.UtcNow);

    public static OperatorAlertNotification Error(string source, string title, string body)
        => new("error", source, title, body, DateTime.UtcNow);
}
