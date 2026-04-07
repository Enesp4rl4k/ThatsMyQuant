using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Institutional-grade circuit breaker and kill switch system.
/// Protects against runaway losses, error cascades, and API rate limit violations.
/// 
/// Three protection layers:
/// 1. Sliding window rate limiter (configurable orders/sec)
/// 2. Drawdown circuit breaker (halts trading when daily loss exceeds threshold)
/// 3. Error cascade halt (kills all orders after consecutive unhandled exceptions)
/// </summary>
public sealed class CircuitBreakerService
{
    private readonly ILogger<CircuitBreakerService> _logger;
    private readonly IPortfolioManager _portfolio;

    // Configuration
    private readonly decimal _maxDrawdownPercent;
    private readonly int _maxConsecutiveErrors;
    private readonly int _maxOrdersPerSecond;

    // Rate limiter state (sliding window via timestamps)
    private readonly long[] _orderTimestamps;
    private int _timestampIndex;

    // State
    private volatile bool _isHalted;
    private int _consecutiveErrors;
    private string _haltReason = "";
    private DateTime _lastHaltTime;
    private long _totalOrdersBlocked;
    private long _totalRateLimitHits;

    public bool IsHalted => _isHalted;
    public string HaltReason => _haltReason;
    public long TotalOrdersBlocked => _totalOrdersBlocked;
    public long TotalRateLimitHits => _totalRateLimitHits;

    public CircuitBreakerService(
        ILogger<CircuitBreakerService> logger,
        IPortfolioManager portfolio,
        decimal maxDrawdownPercent = 5.0m,
        int maxConsecutiveErrors = 3,
        int maxOrdersPerSecond = 10)
    {
        _logger = logger;
        _portfolio = portfolio;
        _maxDrawdownPercent = maxDrawdownPercent;
        _maxConsecutiveErrors = maxConsecutiveErrors;
        _maxOrdersPerSecond = maxOrdersPerSecond;
        _orderTimestamps = new long[maxOrdersPerSecond];
    }

    /// <summary>
    /// Checks all circuit breaker conditions before allowing an order.
    /// Returns (allowed, reason) — if not allowed, reason explains why.
    /// </summary>
    public (bool Allowed, string? BlockReason) CheckOrderAllowed()
    {
        // 1. Manual halt
        if (_isHalted)
        {
            Interlocked.Increment(ref _totalOrdersBlocked);
            return (false, $"HALTED: {_haltReason}");
        }

        // 2. Drawdown circuit breaker
        var drawdown = _portfolio.GetDrawdownPercent();
        if (drawdown >= _maxDrawdownPercent)
        {
            Halt($"Drawdown breaker triggered: {drawdown:F2}% >= {_maxDrawdownPercent}% threshold");
            return (false, _haltReason);
        }

        // 3. Sliding window rate limiter
        long now = Environment.TickCount64;
        int idx = Interlocked.Increment(ref _timestampIndex) % _maxOrdersPerSecond;
        long oldestInWindow = Interlocked.Exchange(ref _orderTimestamps[idx], now);

        if (now - oldestInWindow < 1000) // Within 1-second window
        {
            Interlocked.Increment(ref _totalRateLimitHits);
            Interlocked.Increment(ref _totalOrdersBlocked);
            return (false, $"Rate limited: max {_maxOrdersPerSecond} orders/sec");
        }

        return (true, null);
    }

    /// <summary>
    /// Records a successful execution — resets consecutive error counter.
    /// </summary>
    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveErrors, 0);
    }

    /// <summary>
    /// Records an execution error. Triggers kill switch after max consecutive errors.
    /// </summary>
    public void RecordError(Exception? ex = null)
    {
        int errors = Interlocked.Increment(ref _consecutiveErrors);
        _logger.LogWarning(ex, "Execution error #{Count}/{Max}", errors, _maxConsecutiveErrors);

        if (errors >= _maxConsecutiveErrors)
        {
            Halt($"Error cascade halt: {errors} consecutive failures. Last: {ex?.Message}");
        }
    }

    /// <summary>
    /// Manually halts all trading with a reason.
    /// </summary>
    public void Halt(string reason)
    {
        _isHalted = true;
        _haltReason = reason;
        _lastHaltTime = DateTime.UtcNow;
        Interlocked.Increment(ref _totalOrdersBlocked);
        _logger.LogCritical("🚨 CIRCUIT BREAKER TRIGGERED: {Reason}", reason);
    }

    /// <summary>
    /// Resumes trading after manual review.
    /// </summary>
    public void Resume()
    {
        _isHalted = false;
        _haltReason = "";
        Interlocked.Exchange(ref _consecutiveErrors, 0);
        _logger.LogInformation("✅ Circuit breaker reset — trading resumed at {Time}", DateTime.UtcNow);
    }

    /// <summary>
    /// Returns a diagnostic snapshot of the circuit breaker state.
    /// </summary>
    public CircuitBreakerStatus GetStatus() => new()
    {
        IsHalted = _isHalted,
        HaltReason = _haltReason,
        LastHaltTime = _lastHaltTime,
        ConsecutiveErrors = _consecutiveErrors,
        CurrentDrawdownPercent = _portfolio.GetDrawdownPercent(),
        TotalOrdersBlocked = _totalOrdersBlocked,
        TotalRateLimitHits = _totalRateLimitHits,
        MaxDrawdownThreshold = _maxDrawdownPercent,
        MaxConsecutiveErrorThreshold = _maxConsecutiveErrors,
        MaxOrdersPerSecond = _maxOrdersPerSecond
    };
}

/// <summary>
/// Diagnostic snapshot of circuit breaker state for monitoring.
/// </summary>
public sealed record CircuitBreakerStatus
{
    public bool IsHalted { get; init; }
    public string HaltReason { get; init; } = "";
    public DateTime LastHaltTime { get; init; }
    public int ConsecutiveErrors { get; init; }
    public decimal CurrentDrawdownPercent { get; init; }
    public long TotalOrdersBlocked { get; init; }
    public long TotalRateLimitHits { get; init; }
    public decimal MaxDrawdownThreshold { get; init; }
    public int MaxConsecutiveErrorThreshold { get; init; }
    public int MaxOrdersPerSecond { get; init; }
}
