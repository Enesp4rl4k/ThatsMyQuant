namespace Listener.Api.Endpoints;

public sealed record PortfolioSummaryDto(
    decimal Balance,
    decimal RealizedPnL,
    decimal UnrealizedPnL,
    decimal TotalEquity,
    decimal TotalFeesPaid,
    decimal NetFunding,
    int OpenPositionCount,
    int ClosedTradeCount,
    int WinningTradeCount,
    int LosingTradeCount,
    double WinRate,
    double ProfitFactor,
    double AverageRiskReward,
    decimal AverageTradePnL);

public sealed record PositionDto(
    Guid Id,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal RemainingQuantity,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal UnrealizedPnL,
    double Roe,
    decimal Leverage,
    string LifecycleStatus,
    string ExitPolicyTag,
    string StrategyTag,
    decimal StopLoss,
    decimal TakeProfit,
    DateTime EntryTime);

public sealed record TradeJournalDto(
    Guid Id,
    string Symbol,
    string Side,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal Quantity,
    decimal RealizedPnL,
    decimal PnlPercent,
    string Status,
    string LifecycleStatus,
    string ExecutionMode,
    string ExitReason,
    string StrategyTag,
    DateTime EntryTime,
    DateTime? ExitTime);

public sealed record PositionDetailDto(
    PositionDto Position,
    TradeJournalDto Trade,
    string Reasoning,
    decimal MaxFavorableExcursionPercent,
    decimal MaxAdverseExcursionPercent,
    decimal SignalToFillDriftBps,
    decimal TotalFeesPaid,
    decimal NetFunding,
    string MlModelVersion,
    string PromotionStage);

public sealed record ExchangeHealthDto(
    string Venue,
    bool CredentialsConfigured,
    bool IsConnected,
    bool IsDemoMode,
    decimal? Balance,
    DateTime? LastSuccessUtc,
    DateTime? LastAttemptUtc,
    string? LastError,
    int ConsecutiveFailures);

public sealed record CandleDto(
    string Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed record StrategyBreakdownDto(
    string StrategyTag,
    int TradeCount,
    int WinCount,
    int LossCount,
    double WinRate,
    double ProfitFactor,
    decimal NetPnl,
    decimal AveragePnl,
    double Sharpe,
    decimal MaxWin,
    decimal MaxLoss,
    DateTime LastTradeUtc);

public sealed record SignalSnapshotDto(
    Guid Id,
    string Symbol,
    string Side,
    decimal Price,
    decimal Quantity,
    int Confidence,
    string Reasoning,
    string Status,
    string StrategyTag,
    string RegimeTag,
    string Source,
    string Exchange,
    DateTime Timestamp,
    string ReasonFlags);

public sealed record EquityPointDto(
    string Date,
    decimal DailyPnL,
    decimal CumulativeEquity,
    int TradeCount);

public sealed record PositionCloseRequestDto(
    string Symbol,
    string? Side,        // "buy" | "sell" | null = any side
    string? Reason);

public sealed record PositionCloseResponseDto(
    long Id,
    string Status,
    string Symbol);

public sealed record FlattenAllRequestDto(string? Reason);
