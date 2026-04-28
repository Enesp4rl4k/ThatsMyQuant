namespace ThatsMyQuant.Contracts.Models;

public enum AlertKind
{
    Trade = 0,
    Risk = 1,
    System = 2,
    Promotion = 3,
}

public enum AlertChannel
{
    Telegram = 0,
    Webhook = 1,
    Console = 2,
    Mock = 3,
}

public enum AlertDispatchStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    Skipped = 3,
}

public class AlertHistoryEntry
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public AlertKind Kind { get; set; }
    public AlertChannel Channel { get; set; }
    public AlertDispatchStatus Status { get; set; } = AlertDispatchStatus.Pending;
    public string Symbol { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Metadata { get; set; }
    public string? FailureReason { get; set; }
    public string? CorrelationId { get; set; }
}
