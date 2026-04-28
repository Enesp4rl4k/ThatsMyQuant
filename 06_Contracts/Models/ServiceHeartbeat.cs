namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Cross-process service liveness signal. Each background service writes a row
/// every ~30s. Listener.Api reads these rows for the System Health dashboard.
/// </summary>
public sealed class ServiceHeartbeat
{
    public int Id { get; set; }

    /// <summary>Stable name like "QuantEngine.Worker" or "Execution.Worker".</summary>
    public string ServiceName { get; set; } = string.Empty;

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string Status { get; set; } = "running";
    public long MemoryBytes { get; set; }
}
