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
    public decimal EntrySlippagePercent { get; set; }
    public long ExecutionLatencyMs { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public bool IsClosed => ExitTime.HasValue;
}
