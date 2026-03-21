using MediatR;
using ThatsMyQuant.Domain.Enums;

using System.Text.Json.Serialization;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Domain.Entities;

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

    /// <summary>Strategy confidence score 0-100. Higher = stronger indicator confluence.</summary>
    public int Confidence { get; set; }

    /// <summary>Human-readable reasoning for why this signal was approved or rejected.</summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>Named strategy family responsible for the signal.</summary>
    public string StrategyTag { get; set; } = "TrendConfluence";

    /// <summary>Detected market regime at signal creation time.</summary>
    public string RegimeTag { get; set; } = "Unknown";

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>The rolling 24-hour volume for the asset. Critical for calculating ADV slip-protection.</summary>
    public decimal Volume24h { get; set; } = 1_000_000; // Default to $1M for safety

    public Dictionary<string, string> Metadata { get; set; } = new();
}
