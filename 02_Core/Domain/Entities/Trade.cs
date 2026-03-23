using ThatsMyQuant.Domain.Enums;

namespace ThatsMyQuant.Domain.Entities;

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
    public string ExecutionMode { get; set; } = "REAL";
    public string PromotionStage { get; set; } = "Backtest";
    public decimal EntrySlippagePercent { get; set; }
    public long ExecutionLatencyMs { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public bool IsClosed => ExitTime.HasValue;
}
