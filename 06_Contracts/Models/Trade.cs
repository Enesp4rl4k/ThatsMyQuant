using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Models;

public class Trade
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SignalId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; } = TradeSide.Buy;
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public TradeStatus Status { get; set; } = TradeStatus.Pending;
    public TradeLifecycleStatus LifecycleStatus { get; set; } = TradeLifecycleStatus.Pending;
    public string StrategyTag { get; set; } = "TrendConfluence";
    public string RegimeTag { get; set; } = "Unknown";
    public string ExitPolicyTag { get; set; } = "TrendRunner";
    public decimal EdgeScore { get; set; }
    public string EdgeBand { get; set; } = string.Empty;
    public string ExactBucketImpactLabel { get; set; } = string.Empty;
    public string ExactBucketSummary { get; set; } = string.Empty;
    public decimal FinalAllocatorBias { get; set; }
    public string FinalAllocatorScope { get; set; } = string.Empty;
    public decimal VenueExecutionBias { get; set; }
    public string VenueExecutionScope { get; set; } = string.Empty;
    public string VenueImpactLabel { get; set; } = string.Empty;
    public string ConcordanceImpactLabel { get; set; } = string.Empty;
    public string ConcordanceSummary { get; set; } = string.Empty;
    public bool FeatureDriftDetected { get; set; }
    public bool FeatureDriftCritical { get; set; }
    public int DriftedFeatureCount { get; set; }
    public decimal FeatureDriftMaxZScore { get; set; }
    public string FeatureDriftImpactLabel { get; set; } = string.Empty;
    public string FeatureDriftSummary { get; set; } = string.Empty;
    public string MlModelVersion { get; set; } = string.Empty;
    public string ExecutionMode { get; set; } = "REAL";
    public string PromotionStage { get; set; } = "Backtest";
    public decimal RequestedQuantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal RemainingQuantity { get; set; }
    public decimal ClosedQuantity { get; set; }
    public decimal AverageExitPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal PartialTakeProfit { get; set; }
    public decimal PartialExitFraction { get; set; }
    public decimal BreakevenTriggerFraction { get; set; } = 0.50m;
    public bool IsBreakevenArmed { get; set; }
    public bool HasTakenPartialProfit { get; set; }
    public decimal TrailingStopPrice { get; set; }
    public decimal OptimalPriceSinceEntry { get; set; }
    public decimal WorstPriceSinceEntry { get; set; }
    public decimal MaxFavorableExcursionPercent { get; set; }
    public decimal MaxAdverseExcursionPercent { get; set; }
    public decimal MarkPrice { get; set; }
    public int BarsHeld { get; set; }
    public int MaxHoldingBars { get; set; }
    public string ExitReason { get; set; } = string.Empty;
    public decimal EntrySlippagePercent { get; set; }
    public decimal PositionSizePercent { get; set; }
    public long ExecutionLatencyMs { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime? SubmittedTime { get; set; }
    public DateTime? LastUpdatedTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public bool IsClosed => ExitTime.HasValue || LifecycleStatus is TradeLifecycleStatus.Closed or TradeLifecycleStatus.Cancelled or TradeLifecycleStatus.Rejected or TradeLifecycleStatus.Failed;
    public bool IsPositionOpen => LifecycleStatus is TradeLifecycleStatus.Submitted or TradeLifecycleStatus.Accepted or TradeLifecycleStatus.PartiallyFilled or TradeLifecycleStatus.Active or TradeLifecycleStatus.PartialExit or TradeLifecycleStatus.Closing;
    public decimal RealisedPnL
    {
        get
        {
            if (!IsClosed && ClosedQuantity <= 0m)
            {
                return 0m;
            }

            var realizedQuantity = ClosedQuantity > 0m
                ? ClosedQuantity
                : (IsClosed ? Quantity : 0m);
            var realizedExitPrice = AverageExitPrice > 0m
                ? AverageExitPrice
                : ExitPrice;
            var direction = Side == TradeSide.Sell ? -1m : 1m;

            return (realizedExitPrice - EntryPrice) * realizedQuantity * direction;
        }
    }

    public decimal PnlPercent
    {
        get
        {
            var realizedExitPrice = AverageExitPrice > 0m
                ? AverageExitPrice
                : ExitPrice;
            return EntryPrice > 0m && (IsClosed || ClosedQuantity > 0m)
                ? ((realizedExitPrice - EntryPrice) / EntryPrice) * (Side == TradeSide.Sell ? -1m : 1m)
                : 0m;
        }
    }
}
