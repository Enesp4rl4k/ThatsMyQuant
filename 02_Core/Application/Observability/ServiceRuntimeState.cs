using System.Collections.Concurrent;

namespace ThatsMyQuant.Application.Observability;

public sealed record ServiceRuntimeSnapshot(
    string ServiceName,
    bool IsReady,
    DateTime StartedUtc,
    DateTime? ReadySinceUtc,
    DateTime LastUpdatedUtc,
    IReadOnlyList<string> Reasons);

public sealed class ServiceRuntimeState
{
    private readonly ConcurrentDictionary<string, byte> _reasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private DateTime? _readySinceUtc;
    private bool _isReady;

    public ServiceRuntimeState(string serviceName = "ThatsMyQuant")
    {
        ServiceName = serviceName;
        StartedUtc = DateTime.UtcNow;
        LastUpdatedUtc = StartedUtc;
    }

    public string ServiceName { get; }

    public DateTime StartedUtc { get; }

    public DateTime LastUpdatedUtc { get; private set; }

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _isReady;
            }
        }
    }

    public void MarkReady()
    {
        lock (_gate)
        {
            _isReady = true;
            _readySinceUtc ??= DateTime.UtcNow;
            _reasons.Clear();
            LastUpdatedUtc = DateTime.UtcNow;
        }
    }

    public void MarkNotReady(string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            _reasons[reason] = 0;
        }

        lock (_gate)
        {
            _isReady = false;
            _readySinceUtc = null;
            LastUpdatedUtc = DateTime.UtcNow;
        }
    }

    public ServiceRuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new ServiceRuntimeSnapshot(
                ServiceName,
                _isReady,
                StartedUtc,
                _readySinceUtc,
                LastUpdatedUtc,
                _reasons.Keys.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }
}
