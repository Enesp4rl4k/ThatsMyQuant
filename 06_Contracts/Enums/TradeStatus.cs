namespace ThatsMyQuant.Contracts.Enums;

/// <summary>
/// Pipeline execution status — tracks where a trade is in the processing pipeline.
/// Owned by the execution engine (MediatR handlers, queue workers).
/// Transitions: Pending → Queued → Executed | Rejected | Failed | DeadLetter.
/// </summary>
public enum TradeStatus
{
    Pending,
    Queued,
    Executed,
    Rejected,
    Failed,
    DeadLetter
}
