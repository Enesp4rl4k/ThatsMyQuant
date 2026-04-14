namespace ThatsMyQuant.Infrastructure.Persistence;

public class AuditEntry
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public string Payload { get; set; } = string.Empty;
}
