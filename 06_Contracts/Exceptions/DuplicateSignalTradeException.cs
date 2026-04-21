namespace ThatsMyQuant.Contracts.Exceptions;

/// <summary>
/// Raised when a trade insert loses the race for a unique SignalId claim.
/// </summary>
public sealed class DuplicateSignalTradeException : Exception
{
    public DuplicateSignalTradeException(Guid signalId, Exception? innerException = null)
        : base($"A trade for signal '{signalId}' already exists.", innerException)
    {
        SignalId = signalId;
    }

    public Guid SignalId { get; }
}
