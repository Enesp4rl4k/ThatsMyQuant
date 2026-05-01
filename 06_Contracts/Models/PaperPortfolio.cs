using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Virtual portfolio for paper trading — track positions, balance, and P&L without real money.
/// </summary>
public class PaperPortfolio
{
    // Defaults are zero — paper portfolio MUST be seeded explicitly (from OKX demo balance via
    // ExchangeBalanceSyncHostedService, from a snapshot via PaperPortfolioPersistenceService, or
    // from config). Trading code should refuse to size positions when InitialBalance==0 rather
    // than fall back to a fake $100k that misleads PnL/Sharpe/Kelly computations.
    public decimal InitialBalance { get; set; }
    public decimal Cash { get; set; }
    public List<PaperPosition> OpenPositions { get; set; } = new();
    public List<PaperPosition> ClosedPositions { get; set; } = new();
    public List<decimal> EquityCurve { get; set; } = new();
    public decimal DailyPnL { get; set; }
    public DateTime DailyPnLResetDate { get; set; } = DateTime.UtcNow.Date;

    /// <summary>
    /// Hard cap on EquityCurve length. One entry per closed trade across multiple
    /// hosted services; over months of paper running (and especially when a strategy
    /// fires more often than expected) this list grows unbounded and bloats the JSON
    /// snapshot file. 10,000 entries covers 5+ years of typical trade frequency
    /// (~5 trades/day on a multi-symbol portfolio) and stays under ~200 KB serialized.
    /// </summary>
    public const int EquityCurveMaxEntries = 10_000;

    /// <summary>
    /// Append a new equity reading and trim the oldest entries when the cap is
    /// exceeded. Drop-oldest preserves the recent shape of the curve, which is what
    /// the dashboard / drawdown analytics actually look at.
    /// </summary>
    public void AppendEquity(decimal equity)
    {
        EquityCurve.Add(equity);
        if (EquityCurve.Count > EquityCurveMaxEntries)
        {
            var excess = EquityCurve.Count - EquityCurveMaxEntries;
            EquityCurve.RemoveRange(0, excess);
        }
    }

    public decimal TotalEquity => Cash + OpenPositions.Sum(p => p.IsLeveragedPaperPosition ? p.UnrealizedPnL : p.MarketValue);
    public decimal TotalPnL => TotalEquity - InitialBalance;
    public double TotalPnLPercent => InitialBalance > 0 ? (double)(TotalPnL / InitialBalance * 100) : 0;
    public int TotalTrades => ClosedPositions.Count;
    public int WinningTrades => ClosedPositions.Count(p => p.RealizedPnL > 0);
    public double WinRate => TotalTrades > 0 ? WinningTrades * 100.0 / TotalTrades : 0;

    public void ResetDailyPnLIfNeeded()
    {
        if (DateTime.UtcNow.Date > DailyPnLResetDate)
        {
            DailyPnL = 0;
            DailyPnLResetDate = DateTime.UtcNow.Date;
        }
    }
}

public class PaperPosition
{
    public string Symbol { get; set; } = "";
    public TradeSide Side { get; set; } = TradeSide.Buy;
    public decimal EntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal PartialTakeProfit { get; set; }
    public decimal RemainingQuantity { get; set; }
    public bool HasTakenPartialProfit { get; set; }
    public decimal PartialExitFraction { get; set; }
    public string ExitPolicyTag { get; set; } = "Balanced";
    public decimal BreakevenTriggerFraction { get; set; } = 0.50m;
    public bool IsBreakevenArmed { get; set; }
    public int BarsHeld { get; set; }
    public int MaxHoldingBars { get; set; }
    public string StrategyTag { get; set; } = "TrendConfluence";
    public string RegimeTag { get; set; } = "Unknown";
    public string ExecutionMode { get; set; } = "PAPER";
    public string PromotionStage { get; set; } = "Paper";
    public decimal TrailingStopPrice { get; set; }
    public decimal InitialStopDistance { get; set; }
    public decimal OptimalPriceSinceEntry { get; set; }
    public decimal WorstPriceSinceEntry { get; set; }
    public decimal MaxFavorableExcursionPercent { get; set; }
    public decimal MaxAdverseExcursionPercent { get; set; }
    public DateTime EntryTime { get; set; } = DateTime.UtcNow;
    public DateTime? ExitTime { get; set; }
    public decimal RealizedPnL { get; set; }
    public string ExitReason { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public int Confidence { get; set; }
    public decimal OpenedQuantity { get; set; }
    public decimal Leverage { get; set; } = 1m;
    public MarginMode MarginMode { get; set; } = MarginMode.Isolated;
    public decimal InitialMargin { get; set; }
    public decimal RemainingMargin { get; set; }
    public decimal MaintenanceMarginRate { get; set; } = 0.005m;
    public decimal LiquidationPrice { get; set; }
    public bool IsLeveragedPaperPosition { get; set; }
    public decimal NetFunding { get; set; }
    /// <summary>True when entry was filled as a passive maker limit (no spread/slippage, maker fee).</summary>
    public bool IsMakerEntry { get; set; }

    /// <summary>
    /// Sum of real fees paid to the venue (entry fill + exit fill), in quote currency.
    /// Populated when the position was executed against a real venue and the executor
    /// queried <see cref="IExchangePositionReader.GetLastFillAsync"/> on close. Null
    /// when the position is pure-simulation or the venue didn't return fill data.
    /// TCA / leakage analysis MUST prefer this over any assumed fee rate.
    /// </summary>
    public decimal? RealFeePaid { get; set; }

    /// <summary>
    /// Slippage paid as quote-currency cost: (mark_price - actual_fill_price) × qty,
    /// signed so that "fill worse than mark" is positive (= money lost to slippage).
    /// Sums entry-leg + exit-leg slippage. Null when the executor didn't observe the
    /// mark price at fill time (pure simulation, or mark price unavailable).
    ///
    /// Decomposing leakage into Fee + Slippage + Funding lets TCA / promotion gate
    /// distinguish "venue is expensive" (fee) from "we hit the book at bad prices"
    /// (slippage = strategy's market-impact problem) from "funding accrual against
    /// us" (rate management). The 28→10 bp fee fix was a Fee-bucket lesson; this
    /// field unblocks the Slippage-bucket version of the same audit.
    /// </summary>
    public decimal? RealSlippagePaid { get; set; }

    /// <summary>
    /// Reference mark price observed at entry (mid-of-book or candle close,
    /// whichever the executor used as decision price). Stored alongside the
    /// actual filled <see cref="EntryPrice"/> so TCA can compute slippage
    /// retroactively even on positions where <see cref="RealSlippagePaid"/>
    /// wasn't populated at close time. Null when not captured.
    /// </summary>
    public decimal? EntryMarkPrice { get; set; }

    /// <summary>Same idea for exit leg.</summary>
    public decimal? ExitMarkPrice { get; set; }

    public decimal MarketValue => Side == TradeSide.Buy
        ? Quantity * CurrentPrice
        : -(Quantity * CurrentPrice);
    public decimal UnrealizedPnL => Side == TradeSide.Buy
        ? (CurrentPrice - EntryPrice) * Quantity
        : (EntryPrice - CurrentPrice) * Quantity;
}
