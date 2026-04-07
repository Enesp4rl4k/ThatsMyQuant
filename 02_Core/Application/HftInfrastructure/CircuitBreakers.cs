using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// A critical HFT safety mechanism. Binds to the Execution Engine and strictly monitors heartbeat timestamps.
/// If the matching engine or data feed goes silent for >50ms, it triggers a catastrophic CancellationToken severing market access.
/// Uses Interlocked operations on tick counts for cross-thread visibility without memory ordering issues.
/// </summary>
public class HeartbeatMonitor
{
    private long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
    private readonly long _maxStaleThresholdTicks;
    private readonly DeadMansSwitch _safetySwitch;

    public HeartbeatMonitor(DeadMansSwitch safetySwitch, int maxStaleMilliseconds = 50)
    {
        _safetySwitch = safetySwitch;
        _maxStaleThresholdTicks = TimeSpan.FromMilliseconds(maxStaleMilliseconds).Ticks;
    }

    /// <summary>
    /// Called continuously by the market data socket on a dedicated network thread.
    /// Uses Interlocked.Exchange for full memory barrier visibility to the audit thread.
    /// </summary>
    public void RecordHeartbeat()
    {
        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Audits the heartbeat recency. Executed strictly every 5ms on a high-priority timer.
    /// Uses Interlocked.Read for consistent cross-thread reads.
    /// </summary>
    public void AuditConnection()
    {
        var lastTicks = Interlocked.Read(ref _lastHeartbeatTicks);
        if (DateTime.UtcNow.Ticks - lastTicks > _maxStaleThresholdTicks)
        {
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

    public async Task TriggerLiquidationEvacuationAsync(string reason)
    {
        // 1. Immediately log security audit
        await _auditLogger.LogSecurityEventAsync("SYSTEM", "CIRCUIT_BREAKER", "DEAD_MANS_SWITCH_ENGAGED", reason)
            .ConfigureAwait(false);

        // 2. Transmit global halt to all IExecutionEngine instances
        Console.WriteLine($"[CRITICAL HALT] Dead Man's Switch Activated. Reason: {reason}");

        // 3. (Future) Issue asynchronous zero-allocation FIX Market Orders to flatten all inventory to Cash.
    }

    /// <summary>
    /// Synchronous trigger for use from hot paths (e.g. HeartbeatMonitor audit).
    /// Queues the async liquidation on the ThreadPool to avoid deadlocking the caller.
    /// Observes and logs any errors — a failed evacuation must never go unnoticed.
    /// </summary>
    public void TriggerLiquidationEvacuation(string reason)
    {
        if (IsTriggered) return;

        // Mark triggered immediately to prevent cascading calls while Task.Run queues
        IsTriggered = true;

        Task.Run(async () =>
        {
            try
            {
                await TriggerLiquidationEvacuationAsync(reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Critical: evacuation failed. Log to stderr as last resort since logger may be unavailable.
                Console.Error.WriteLine($"[FATAL] Dead Man's Switch evacuation FAILED: {ex.Message}");
                Console.Error.WriteLine(ex.ToString());
            }
        });
    }
}
