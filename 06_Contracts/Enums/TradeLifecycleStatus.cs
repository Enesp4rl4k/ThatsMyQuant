namespace ThatsMyQuant.Contracts.Enums;

/// <summary>
/// Position lifecycle status — tracks the state of an open/closed position on the exchange.
/// Owned by the position supervisor and exchange execution services.
/// Transitions: Pending → Submitted → Accepted → PartiallyFilled → Active → PartialExit → Closing → Closed.
/// Terminal states: Closed, Cancelled, Rejected, Failed.
/// </summary>
public enum TradeLifecycleStatus
{
    Pending,
    Submitted,
    Accepted,
    PartiallyFilled,
    Active,
    PartialExit,
    Closing,
    Closed,
    Cancelled,
    Rejected,
    Failed
}

/// <summary>
/// Maps between pipeline execution status and position lifecycle status.
/// </summary>
public static class TradeStatusExtensions
{
    /// <summary>
    /// Derives the appropriate lifecycle status from a pipeline execution status.
    /// </summary>
    public static TradeLifecycleStatus ToLifecycleStatus(this TradeStatus status) => status switch
    {
        TradeStatus.Pending => TradeLifecycleStatus.Pending,
        TradeStatus.Queued => TradeLifecycleStatus.Submitted,
        TradeStatus.Executed => TradeLifecycleStatus.Active,
        TradeStatus.Rejected => TradeLifecycleStatus.Rejected,
        TradeStatus.Failed => TradeLifecycleStatus.Failed,
        TradeStatus.DeadLetter => TradeLifecycleStatus.Failed,
        _ => TradeLifecycleStatus.Pending
    };

    /// <summary>
    /// Whether this lifecycle status represents a terminal (no further transitions) state.
    /// </summary>
    public static bool IsTerminal(this TradeLifecycleStatus status) => status is
        TradeLifecycleStatus.Closed or
        TradeLifecycleStatus.Cancelled or
        TradeLifecycleStatus.Rejected or
        TradeLifecycleStatus.Failed;

    /// <summary>
    /// Validates whether a transition from the current state to the target state is legal.
    /// Prevents invalid state transitions (e.g. Closed → Active).
    /// </summary>
    public static bool CanTransitionTo(this TradeLifecycleStatus current, TradeLifecycleStatus target)
    {
        if (current == target) return true;
        if (current.IsTerminal()) return false; // Terminal states cannot transition

        return (current, target) switch
        {
            (TradeLifecycleStatus.Pending, TradeLifecycleStatus.Submitted) => true,
            (TradeLifecycleStatus.Pending, TradeLifecycleStatus.Cancelled) => true,
            (TradeLifecycleStatus.Pending, TradeLifecycleStatus.Rejected) => true,
            (TradeLifecycleStatus.Pending, TradeLifecycleStatus.Failed) => true,

            (TradeLifecycleStatus.Submitted, TradeLifecycleStatus.Accepted) => true,
            (TradeLifecycleStatus.Submitted, TradeLifecycleStatus.Rejected) => true,
            (TradeLifecycleStatus.Submitted, TradeLifecycleStatus.Cancelled) => true,
            (TradeLifecycleStatus.Submitted, TradeLifecycleStatus.Failed) => true,

            (TradeLifecycleStatus.Accepted, TradeLifecycleStatus.PartiallyFilled) => true,
            (TradeLifecycleStatus.Accepted, TradeLifecycleStatus.Active) => true,
            (TradeLifecycleStatus.Accepted, TradeLifecycleStatus.Cancelled) => true,
            (TradeLifecycleStatus.Accepted, TradeLifecycleStatus.Failed) => true,

            (TradeLifecycleStatus.PartiallyFilled, TradeLifecycleStatus.Active) => true,
            (TradeLifecycleStatus.PartiallyFilled, TradeLifecycleStatus.Cancelled) => true,
            (TradeLifecycleStatus.PartiallyFilled, TradeLifecycleStatus.Failed) => true,

            (TradeLifecycleStatus.Active, TradeLifecycleStatus.PartialExit) => true,
            (TradeLifecycleStatus.Active, TradeLifecycleStatus.Closing) => true,
            (TradeLifecycleStatus.Active, TradeLifecycleStatus.Closed) => true,
            (TradeLifecycleStatus.Active, TradeLifecycleStatus.Failed) => true,

            (TradeLifecycleStatus.PartialExit, TradeLifecycleStatus.Closing) => true,
            (TradeLifecycleStatus.PartialExit, TradeLifecycleStatus.Closed) => true,
            (TradeLifecycleStatus.PartialExit, TradeLifecycleStatus.Failed) => true,

            (TradeLifecycleStatus.Closing, TradeLifecycleStatus.Closed) => true,
            (TradeLifecycleStatus.Closing, TradeLifecycleStatus.Failed) => true,

            _ => false
        };
    }
}
