namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Represents a direct market access (DMA) connection utilizing the Financial Information eXchange (FIX) protocol.
/// Bypasses REST overhead for ultra-low latency execution scenarios typical in institutional environments.
/// </summary>
public interface IFixConnectionManager
{
    Task ConnectAsync(string targetCompId, string senderCompId, string host, int port, CancellationToken cancellationToken);
    Task DisconnectAsync();
    bool IsConnected { get; }
    
    /// <summary>
    /// Transmits a pre-serialized zero-allocation span representing a FIX 4.4 / 5.0 message payload directly over TCP.
    /// </summary>
    Task SendRawMessageAsync(ReadOnlyMemory<byte> fixPayload);

    /// <summary>
    /// Occurs when a raw FIX message sequence arrives from the matching engine.
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>> OnMessageReceived;
}

public interface IFixMessageParser
{
    /// <summary>
    /// Reconstitutes a raw byte stream into a structured FIX execution report or market data snapshot without allocations.
    /// </summary>
    bool TryParse(ReadOnlySpan<byte> buffer, out string messageType, out string body);
}
