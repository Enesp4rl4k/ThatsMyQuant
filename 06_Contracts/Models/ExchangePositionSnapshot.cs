namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Lightweight read-only snapshot of an open position on the exchange, used by
/// reconciliation to compare against the local <see cref="PositionState"/> book.
/// </summary>
public sealed record ExchangePositionSnapshot(
    string Symbol,
    decimal Quantity,
    bool IsLong,
    decimal? AvgEntryPrice = null);
