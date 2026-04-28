using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Portfolio-level circuit breaker. Monitors realised drawdown and TRIPS when DD exceeds
/// <see cref="DrawdownTripThreshold"/>, pausing all new entries. Reset is MANUAL (operator
/// review required) — prevents the bot from re-arming itself during a bad regime.
///
/// Three states:
///   • Armed       — trading allowed, DD within budget
///   • Warning     — DD ≥ warning threshold (default 60% of trip), alert but keep trading
///   • Tripped     — DD ≥ trip threshold; ALL new entries blocked until operator resets
///
/// Thread-safe via lock. Singleton in DI — the whole system checks its state before opening.
/// </summary>
public sealed class CircuitBreakerMonitor
{
    private readonly ILogger<CircuitBreakerMonitor> _logger;
    private readonly MediatR.IMediator? _mediator;
    private readonly object _lock = new();

    private decimal _peakEquity;
    private decimal _currentEquity;
    private decimal _realizedDrawdown;
    private CircuitBreakerState _state = CircuitBreakerState.Armed;
    private string _lastTripReason = "";
    private DateTime _lastTripUtc;

    /// <summary>Trip at 5% portfolio DD. Hard-coded — operator can't override.</summary>
    public decimal DrawdownTripThreshold { get; init; } = 0.05m;
    /// <summary>Warn at 60% of trip threshold (3% default). Alert-only, not blocking.</summary>
    public decimal DrawdownWarnThreshold { get; init; } = 0.03m;

    public CircuitBreakerMonitor(ILogger<CircuitBreakerMonitor> logger, MediatR.IMediator? mediator = null)
    {
        _logger = logger;
        _mediator = mediator;
    }

    public CircuitBreakerSnapshot Snapshot()
    {
        lock (_lock)
            return new CircuitBreakerSnapshot(_state, _realizedDrawdown, _peakEquity, _currentEquity, _lastTripReason, _lastTripUtc);
    }

    public bool IsTradingAllowed()
    {
        lock (_lock) return _state != CircuitBreakerState.Tripped;
    }

    /// <summary>Feed in latest portfolio equity. Recomputes DD and transitions state.</summary>
    public CircuitBreakerState UpdateEquity(decimal equity)
    {
        lock (_lock)
        {
            _currentEquity = equity;
            if (equity > _peakEquity) _peakEquity = equity;
            _realizedDrawdown = _peakEquity > 0 ? (_peakEquity - equity) / _peakEquity : 0m;

            if (_state == CircuitBreakerState.Tripped) return _state;   // manual reset required

            if (_realizedDrawdown >= DrawdownTripThreshold)
            {
                _state = CircuitBreakerState.Tripped;
                _lastTripReason = $"Portfolio DD {_realizedDrawdown:P2} ≥ trip threshold {DrawdownTripThreshold:P2}";
                _lastTripUtc = DateTime.UtcNow;
                _logger.LogError("CIRCUIT BREAKER TRIPPED: {Reason}. Peak={Peak:N2} Current={Cur:N2}. ALL NEW ENTRIES BLOCKED + existing positions will be flattened.",
                    _lastTripReason, _peakEquity, _currentEquity);

                // 2026-04-24 Critical gap fix: emit event so execution layer flattens open positions.
                // Without this, a bleeding position keeps growing the DD past the trip level.
                // Fire-and-forget — circuit breaker is called from hot paths, can't await.
                if (_mediator is not null)
                {
                    var notification = new CircuitBreakerTrippedNotification(
                        Reason: _lastTripReason,
                        RealizedDrawdown: _realizedDrawdown,
                        PeakEquity: _peakEquity,
                        CurrentEquity: _currentEquity,
                        TrippedUtc: _lastTripUtc);
                    _ = Task.Run(async () =>
                    {
                        try { await _mediator.Publish(notification).ConfigureAwait(false); }
                        catch (Exception ex) { _logger.LogError(ex, "Failed to publish circuit breaker trip event"); }
                    });
                }
            }
            else if (_realizedDrawdown >= DrawdownWarnThreshold)
            {
                if (_state != CircuitBreakerState.Warning)
                    _logger.LogWarning("Circuit breaker WARNING: DD {DD:P2} ≥ warn threshold {Warn:P2}",
                        _realizedDrawdown, DrawdownWarnThreshold);
                _state = CircuitBreakerState.Warning;
            }
            else
            {
                _state = CircuitBreakerState.Armed;
            }

            return _state;
        }
    }

    /// <summary>
    /// Manual reset. Caller should log operator identity. Re-arms the breaker and resets
    /// peak to current (fresh high-water mark). Will not help if positions are still bleeding.
    /// </summary>
    public void ResetTrip(string operatorNote)
    {
        lock (_lock)
        {
            _logger.LogWarning("Circuit breaker RESET by operator. Note: {Note}. Prior trip: {Trip}",
                operatorNote, _lastTripReason);
            _state = CircuitBreakerState.Armed;
            _peakEquity = _currentEquity;
            _realizedDrawdown = 0m;
            _lastTripReason = "";
        }
    }
}

public enum CircuitBreakerState
{
    Armed,
    Warning,
    Tripped
}

public sealed record CircuitBreakerSnapshot(
    CircuitBreakerState State,
    decimal RealizedDrawdown,
    decimal PeakEquity,
    decimal CurrentEquity,
    string LastTripReason,
    DateTime LastTripUtc);

/// <summary>
/// Event emitted when the circuit breaker trips. Subscribers (e.g. PaperTradingService,
/// Live execution layer) should flatten ALL open positions and mark themselves inactive
/// until the breaker is reset.
///
/// Phase A gap 2026-04-24: the breaker previously only blocked NEW entries. A position
/// already bleeding kept growing the DD after trip, defeating the purpose. This event
/// closes that gap by asking the execution layer to flatten.
/// </summary>
public sealed record CircuitBreakerTrippedNotification(
    string Reason,
    decimal RealizedDrawdown,
    decimal PeakEquity,
    decimal CurrentEquity,
    DateTime TrippedUtc) : MediatR.INotification;
