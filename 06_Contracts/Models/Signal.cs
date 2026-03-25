using MediatR;
using ThatsMyQuant.Contracts.Enums;

using System.Text.Json.Serialization;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Models;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Signal))]
[JsonSerializable(typeof(List<Signal>))]
public partial class SignalJsonContext : JsonSerializerContext
{
}

public class Signal : INotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = string.Empty; // e.g., "TradingView", "PMaxIndicator"
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; } = TradeSide.Buy;
    public decimal Price { get; set; }
    public TradeStatus Status { get; set; } = TradeStatus.Pending;
    public int RetryCount { get; set; } = 0;
    public decimal Quantity { get; set; } = 0m;

    /// <summary>Strategy confidence score 0-100. Higher = stronger indicator confluence.</summary>
    public int Confidence { get; set; }

    /// <summary>Human-readable reasoning for why this signal was approved or rejected.</summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>Named strategy family responsible for the signal.</summary>
    public string StrategyTag { get; set; } = "TrendConfluence";

    /// <summary>Detected market regime at signal creation time.</summary>
    public string RegimeTag { get; set; } = "Unknown";

    /// <summary>The market this signal belongs to. Used for market hours and routing decisions.</summary>
    public MarketType MarketType { get; set; } = MarketType.BIST;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>The rolling 24-hour volume for the asset. 0 means unknown — volume checks will be skipped.</summary>
    public decimal Volume24h { get; set; } = 0;

    // Ultra-Low Latency Order Book Microstructure Data
    public double? OrderFlowImbalance { get; set; }
    public double? BestBid { get; set; }
    public double? BestAsk { get; set; }
    public double? WallAgeMs { get; set; }

    public ExecutionActionType ExecutionAction { get; set; } = ExecutionActionType.None;

    public PriceActionType PriceActionSignal { get; set; } = PriceActionType.None;

    public SignalReasonFlags ReasonFlags { get; set; } = SignalReasonFlags.None;

    public void AddReason(SignalReasonFlags flag)
    {
        ReasonFlags |= flag;
    }

    public Dictionary<string, string> Metadata { get; set; } = new();
}
