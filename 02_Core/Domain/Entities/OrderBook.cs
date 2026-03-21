namespace ThatsMyQuant.Domain.Entities;

/// <summary>
/// Represents a single price level on the L2 Order Book.
/// Using 'readonly record struct' ensures zero-allocation memory for HFT.
/// </summary>
public readonly record struct OrderBookLevel(
    decimal Price,
    decimal Quantity
);

/// <summary>
/// Represents an Order Book snapshot.
/// Struct containing Span or Array for zero allocation.
/// </summary>
public record struct OrderBookSnapshot
{
    public string Symbol { get; init; }
    public long TimestampMs { get; init; }
    public OrderBookLevel[] Bids { get; init; }
    public OrderBookLevel[] Asks { get; init; }

    public OrderBookSnapshot(string symbol, long timestampMs, OrderBookLevel[] bids, OrderBookLevel[] asks)
    {
        Symbol = symbol;
        TimestampMs = timestampMs;
        Bids = bids;
        Asks = asks;
    }
}
