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

    public decimal MarketValue => Side == TradeSide.Buy
        ? Quantity * CurrentPrice
        : -(Quantity * CurrentPrice);
    public decimal UnrealizedPnL => Side == TradeSide.Buy
        ? (CurrentPrice - EntryPrice) * Quantity
        : (EntryPrice - CurrentPrice) * Quantity;
}
