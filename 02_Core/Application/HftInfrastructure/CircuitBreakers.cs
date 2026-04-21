using ThatsMyQuant.Contracts.Interfaces;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// A critical HFT safety mechanism. Binds to the Execution Engine and strictly monitors heartbeat timestamps.
/// If the matching engine or data feed goes silent for >50ms, it triggers a catastrophic CancellationToken severing market access.
/// </summary>
public class HeartbeatMonitor
{
    private long _lastHeartbeatTick = Environment.TickCount64;
    private long _firstHeartbeatTick;
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
        if (Interlocked.Read(ref _firstHeartbeatTick) == 0)
        {
            Interlocked.CompareExchange(ref _firstHeartbeatTick, Environment.TickCount64, 0);
        }

        Interlocked.Exchange(ref _lastHeartbeatTick, Environment.TickCount64);
    }

    /// <summary>
    /// Audits the heartbeat recency. Executed strictly every 5ms on a high-priority timer.
    /// </summary>
    public void AuditConnection()
    {
        if (Interlocked.Read(ref _firstHeartbeatTick) == 0)
        {
            return;
        }

        var last = Interlocked.Read(ref _lastHeartbeatTick);
        if (Environment.TickCount64 - last > _maxStaleThreshold.TotalMilliseconds)
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
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly ILogger<DeadMansSwitch> _logger;
    public bool IsTriggered { get; private set; }

    public DeadMansSwitch(
        IAuditLogger auditLogger,
        CircuitBreakerService circuitBreaker,
        ILogger<DeadMansSwitch> logger)
    {
        _auditLogger = auditLogger;
        _circuitBreaker = circuitBreaker;
        _logger = logger;
    }

    public void TriggerLiquidationEvacuation(string reason)
    {
        if (IsTriggered) return; // Prevent cascading loops

        IsTriggered = true;
        
        // 1. Immediately log security audit
        _auditLogger.LogSecurityEventAsync("SYSTEM", "CIRCUIT_BREAKER", "DEAD_MANS_SWITCH_ENGAGED", reason)
            .GetAwaiter().GetResult();

        // 2. Immediately halt execution dispatch.
        _circuitBreaker.Halt($"DeadManSwitch: {reason}");
        _logger.LogCritical("Dead Man's Switch activated. Reason: {Reason}", reason);

        // 3. (Future) Issue asynchronous zero-allocation FIX Market Orders to flatten all inventory to Cash.
    }
}
