using System.Text.Json.Serialization;

namespace ThatsMyQuant.Contracts.Models;

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

/// <summary>Source-generated JSON context for zero-reflection tick serialization.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(MarketTick))]
public partial class MarketTickJsonContext : JsonSerializerContext;
