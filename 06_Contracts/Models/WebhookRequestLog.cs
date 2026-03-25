namespace ThatsMyQuant.Contracts.Models;

public class WebhookRequestLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
}
