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

/// <summary>
/// [2026-05-16] Read-only snapshot of a CLOSED position from venue history.
/// Used by DbToPortfolioSync to mirror OKX-side closed positions into
/// PaperPortfolio.ClosedPositions so monitors (digest, TCA, ShadowAudit,
/// per-strategy attribution) see live trading reality not just stale paper-mode
/// history. OKX-source: /api/v5/account/positions-history (3-month window).
/// </summary>
public sealed record ClosedPositionSnapshot(
    string Symbol,
    bool WasLong,
    decimal Quantity,
    decimal OpenAvgPrice,
    decimal CloseAvgPrice,
    DateTime OpenTimeUtc,
    DateTime CloseTimeUtc,
    decimal RealizedPnl,
    decimal FeePaid,
    string? StrategyTag = null);
