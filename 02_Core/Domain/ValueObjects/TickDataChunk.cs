namespace ThatsMyQuant.Domain.ValueObjects;

/// <summary>
/// Represents a raw Level 3 Orderbook Tick (Every single trade execution or order placement).
/// Designed as a struct to live entirely on the stack or inline within arrays.
/// </summary>
public readonly struct MicrosecondTick
{
    public readonly long EpochMicroseconds { get; init; }
    public readonly double Price { get; init; }
    public readonly int Quantity { get; init; }
    public readonly byte Side { get; init; } // 1 for Buy, 2 for Sell

    public MicrosecondTick(long epochMicroseconds, double price, int quantity, byte side)
    {
        EpochMicroseconds = epochMicroseconds;
        Price = price;
        Quantity = quantity;
        Side = side;
    }
}

/// <summary>
/// A contiguous block of ticks loaded from a high-density, columnar storage format (like Parquet).
/// By grouping into chunks, we prevent Out-Of-Memory exceptions when processing terabytes of 5-year data.
/// </summary>
public class TickDataChunk
{
    public required string Symbol { get; set; }
    public required MicrosecondTick[] Ticks { get; set; }
    public long StartEpoch { get; set; }
    public long EndEpoch { get; set; }
}
