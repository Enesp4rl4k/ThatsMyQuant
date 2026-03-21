using System.Buffers.Binary;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// A strict ref-struct parser designed to run completely on the Application Thread's stack memory.
/// Used to deserialize ITCH/FIX Market Data directly from a raw byte span without triggering the Garbage Collector.
/// </summary>
public ref struct L2BookParser
{
    private ReadOnlySpan<byte> _buffer;

    public L2BookParser(ReadOnlySpan<byte> tcpPayload)
    {
        _buffer = tcpPayload;
    }

    /// <summary>
    /// Deserializes a mock 16-byte binary ITCH payload:
    /// [0-3] MessageType (4 bytes)
    /// [4-11] Price (8 bytes, Double)
    /// [12-15] Quantity (4 bytes, Int)
    /// </summary>
    public bool TryParseAddOrder(out double price, out int quantity)
    {
        price = 0;
        quantity = 0;

        if (_buffer.Length < 16) return false;

        // Slice without allocating arrays
        var typeSpan = _buffer.Slice(0, 4);
        int messageType = BinaryPrimitives.ReadInt32LittleEndian(typeSpan);

        if (messageType != 1) return false; // Not an 'Add Order' message

        // Read 8-byte double for explicit price
        price = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(4, 8)));
        
        // Read 4-byte int for execution volume
        quantity = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(12, 4));

        return true;
    }
}
