using MediatR;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Notifications;

/// <summary>
/// Fired when a position closes (any mode: backtest/paper/live). Carries the realised
/// PnL and identifying tags so downstream subscribers can attribute outcomes without
/// coupling to the execution pipeline.
///
/// Known subscribers:
///   • ChampionChallengerService — paired outcome recording for shadow promotion.
///   • ExecutionParityService    — outcome-side parity (when the outcome itself diverges
///     between paper and live beyond the entry-fill divergence).
///   • StrategyPromotionService  — rolling performance attribution.
/// </summary>
public sealed record PositionClosedNotification(
    string Symbol,
    string StrategyTag,
    string RegimeTag,
    string ExitPolicyTag,
    string ExitReason,
    string ExecutionMode,
    TradeSide Side,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal Quantity,
    decimal NetPnl,
    decimal NetPnlPercent,
    DateTime EntryTimeUtc,
    DateTime ExitTimeUtc,
    string? CorrelationId = null) : INotification;
