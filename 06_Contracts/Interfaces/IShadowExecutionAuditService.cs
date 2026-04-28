using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Pre-promotion sanity check that re-walks every backtest trade through a realistic
/// execution model (square-root market impact + spread + commission) and reports how
/// much of the backtest PnL survives. Strategies whose edge collapses under shadow
/// execution were never real — they were artifacts of optimistic fills.
///
/// Intended call site: BacktestOrchestrator, AFTER backtest completes, BEFORE invoking
/// FormalPromotionGate.EvaluateBacktestToPaper. If verdict is Reject/Critical, skip
/// promotion regardless of headline Sharpe.
/// </summary>
public interface IShadowExecutionAuditService
{
    ShadowAuditReport Audit(
        BacktestResult backtest,
        IReadOnlyList<Candle> candles,
        ShadowAuditOptions? options = null);
}

public sealed record ShadowAuditOptions
{
    /// <summary>Fallback notional when a trade's PnL/PnLPercent can't infer it.</summary>
    public decimal AssumedNotionalPerTrade { get; init; } = 1_000m;

    /// <summary>Extra commission added on top of the impact model. OKX taker spot = 8 bps.</summary>
    public decimal AdditionalCommissionBps { get; init; } = 8m;

    /// <summary>Half-spread paid on each leg in addition to impact.</summary>
    public decimal AdditionalSpreadBps { get; init; } = 3m;

    /// <summary>Bars summed around an entry to approximate daily volume (24 on a 1h tf).</summary>
    public int VolumeWindowBarsForDailyVolume { get; init; } = 24;

    /// <summary>Used when a candle's bar range is degenerate.</summary>
    public decimal DefaultDailyVolatilityPct { get; init; } = 0.02m;

    /// <summary>Shadow PnL below (1 - this) of backtest PnL → Warn.</summary>
    public double WarnLeakageThresholdPct { get; init; } = 20.0;

    /// <summary>Shadow PnL below (1 - this) of backtest PnL → Reject.</summary>
    public double RejectLeakageThresholdPct { get; init; } = 50.0;

    public static ShadowAuditOptions Default { get; } = new();
}

public sealed record ShadowTradeComparison(
    DateTime EntryTime,
    string Strategy,
    decimal BacktestEntryPrice,
    decimal ShadowEntryPrice,
    decimal BacktestExitPrice,
    decimal ShadowExitPrice,
    decimal BacktestPnL,
    decimal ShadowPnL,
    decimal LeakageDollar,
    decimal LeakageBps);

public enum ShadowAuditVerdict
{
    Pass,
    Warn,
    Reject,
    Critical,
    Insufficient
}

public sealed record ShadowAuditReport(
    int TradesEvaluated,
    int TradesUnmatched,
    decimal BacktestTotalPnL,
    decimal ShadowTotalPnL,
    decimal LeakageDollar,
    double LeakagePercent,
    int BacktestWinners,
    int ShadowWinners,
    double BacktestWinRate,
    double ShadowWinRate,
    double BacktestSharpe,
    double ShadowSharpe,
    double BacktestProfitFactor,
    double ShadowProfitFactor,
    decimal AverageLeakageBpsPerTrade,
    ShadowAuditVerdict Verdict,
    string Recommendation,
    IReadOnlyList<ShadowTradeComparison> TradeDetails);
