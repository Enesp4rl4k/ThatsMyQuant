using System.Text.Json.Serialization;

namespace ThatsMyQuant.Contracts.Models;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(MarketTick))]
public partial class MarketTickJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Represents a single real-time market tick.
/// Using 'readonly record struct' ensures zero-allocation and immutability for HFT.
/// </summary>
public readonly record struct MarketTick(
    string Symbol,
    decimal Price,
    decimal Volume,
    long TimestampMs,
    bool IsBuyerMaker
);
