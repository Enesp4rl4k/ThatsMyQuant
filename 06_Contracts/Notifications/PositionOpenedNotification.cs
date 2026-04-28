using MediatR;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Notifications;

/// <summary>
/// Fired when a position opens (any mode: backtest/paper/live). Carries entry,
/// SL, TP, sizing and reasoning so downstream subscribers — Telegram push,
/// Dashboard live feed, audit log — can render a complete trade record.
/// </summary>
public sealed record PositionOpenedNotification(
    string Symbol,
    string StrategyTag,
    string RegimeTag,
    string ExitPolicyTag,
    string ExecutionMode,
    TradeSide Side,
    decimal EntryPrice,
    decimal Quantity,
    decimal StopLoss,
    decimal TakeProfit,
    decimal EdgeScore,
    decimal Confidence,
    string Reasoning,
    DateTime EntryTimeUtc,
    string? CorrelationId = null) : INotification;
