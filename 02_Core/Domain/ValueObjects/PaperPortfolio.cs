namespace ThatsMyQuant.Domain.ValueObjects;

/// <summary>
/// Virtual portfolio for paper trading — track positions, balance, and P&L without real money.
/// </summary>
public class PaperPortfolio
{
    public decimal InitialBalance { get; set; } = 100_000m;
    public decimal Cash { get; set; } = 100_000m;
    public List<PaperPosition> OpenPositions { get; set; } = new();
    public List<PaperPosition> ClosedPositions { get; set; } = new();
    public List<decimal> EquityCurve { get; set; } = new();
    public decimal DailyPnL { get; set; }
    public DateTime DailyPnLResetDate { get; set; } = DateTime.UtcNow.Date;

    public decimal TotalEquity => Cash + OpenPositions.Sum(p => p.MarketValue);
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
    public string Side { get; set; } = "Buy";
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
    public decimal TrailingStopPrice { get; set; }
    public decimal HighestPriceSinceEntry { get; set; }
    public DateTime EntryTime { get; set; } = DateTime.UtcNow;
    public DateTime? ExitTime { get; set; }
    public decimal RealizedPnL { get; set; }
    public string ExitReason { get; set; } = "";

    public decimal MarketValue => Quantity * CurrentPrice;
    public decimal UnrealizedPnL => Side == "Buy"
        ? (CurrentPrice - EntryPrice) * Quantity
        : (EntryPrice - CurrentPrice) * Quantity;
}
