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

    /// <summary>
    /// Extra commission added on top of the impact model. 2026-05-01: dropped to 0 by
    /// default. Trade.PnL on a paper-or-live close already reflects net-of-fees PnL
    /// (post-T0.3 commit 4004f67: RealFeePaid is captured from venue and applied to
    /// netPnl by the executor). Adding 8 bps here on top double-counted fees ~3× on
    /// the 2026-05-01 ShadowAudit run that flagged 137% aggregate leakage — the
    /// alert was an artifact of the cost model mismatch, not real edge erosion.
    /// Operators who want to test "what if fees were higher" can pass an explicit
    /// override; the default no longer assumes the trade PnL is gross.
    /// </summary>
    public decimal AdditionalCommissionBps { get; init; } = 0m;

    /// <summary>
    /// Half-spread on each leg in addition to impact. 2026-05-01: dropped to 0 by
    /// default for the same reason as AdditionalCommissionBps — the trade fill price
    /// was the actual venue fill (Trade.EntryPrice / ExitPrice), so the spread was
    /// already paid in those numbers. Square-root market-impact model still simulates
    /// what the fill WOULD have been at larger size, which is the audit's real value.
    /// </summary>
    public decimal AdditionalSpreadBps { get; init; } = 0m;

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
