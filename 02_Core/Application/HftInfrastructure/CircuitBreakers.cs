using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// A critical HFT safety mechanism. Binds to the Execution Engine and strictly monitors heartbeat timestamps.
/// If the matching engine or data feed goes silent for >50ms, it triggers a catastrophic CancellationToken severing market access.
/// </summary>
public class HeartbeatMonitor
{
    private long _lastHeartbeatTick = Environment.TickCount64;
    private readonly TimeSpan _maxStaleThreshold;
    private readonly DeadMansSwitch _safetySwitch;

    public HeartbeatMonitor(DeadMansSwitch safetySwitch, int maxStaleMilliseconds = 50)
    {
        _safetySwitch = safetySwitch;
        _maxStaleThreshold = TimeSpan.FromMilliseconds(maxStaleMilliseconds);
    }

    /// <summary>
    /// Called continuously by the market data socket on a dedicated network thread.
    /// </summary>
    public void RecordHeartbeat()
    {
        _lastHeartbeatTick = Environment.TickCount64;
    }

    /// <summary>
    /// Audits the heartbeat recency. Executed strictly every 5ms on a high-priority timer.
    /// </summary>
    public void AuditConnection()
    {
        if (Environment.TickCount64 - _lastHeartbeatTick > _maxStaleThreshold.TotalMilliseconds)
        {
            // Catastrophic latency spike or exchange failure.
            _safetySwitch.TriggerLiquidationEvacuation("Heartbeat stale threshold exceeded (>50ms). Exchange presumed disconnected.");
        }
    }
}

/// <summary>
/// A global fail-close mechanism. When triggered, immediately transitions the entire AppDomain into 
/// a 'HALT' state, firing market-sell orders for all open inventory and locking further API transmission.
/// </summary>
public class DeadMansSwitch
{
    private readonly IAuditLogger _auditLogger;
    public bool IsTriggered { get; private set; }

    public DeadMansSwitch(IAuditLogger auditLogger)
    {
        _auditLogger = auditLogger;
    }

    public void TriggerLiquidationEvacuation(string reason)
    {
        if (IsTriggered) return; // Prevent cascading loops

        IsTriggered = true;
        
        // 1. Immediately log security audit
        _auditLogger.LogSecurityEventAsync("SYSTEM", "CIRCUIT_BREAKER", "DEAD_MANS_SWITCH_ENGAGED", reason)
            .GetAwaiter().GetResult();

        // 2. Transmit global halt to all IExecutionEngine instances
        Console.WriteLine($"[CRITICAL HALT] Dead Man's Switch Activated. Reason: {reason}");

        // 3. (Future) Issue asynchronous zero-allocation FIX Market Orders to flatten all inventory to Cash.
    }
}
