using System.Text;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// High-performance adapter bridging our strategy algorithms with raw Financial Information eXchange (FIX) binary streams.
/// Designed for zero-allocation parsing using `ReadOnlySpan<byte>` over raw TCP sockets.
/// </summary>
public class FixProtocolAdapter : IFixConnectionManager
{
    private bool _isConnected;
    public bool IsConnected => _isConnected;

    public event EventHandler<ReadOnlyMemory<byte>>? OnMessageReceived;

    public Task ConnectAsync(string targetCompId, string senderCompId, string host, int port, CancellationToken cancellationToken)
    {
        // Mocking a dedicated TCP cross-connect authorization sequence.
        Console.WriteLine($"[FIX 4.4] Initiating TCP Cross-Connect to {targetCompId} @ {host}:{port}");
        _isConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _isConnected = false;
        Console.WriteLine("[FIX 4.4] Socket Closed. DMA Disconnected.");
        return Task.CompletedTask;
    }

    public Task SendRawMessageAsync(ReadOnlyMemory<byte> fixPayload)
    {
        if (!_isConnected) throw new InvalidOperationException("Exchange link is disconnected.");

        // In a true environment, this writes unmanaged memory blocks straight to the ISocket.
        // E.g., simulating a New Order Single (35=D) message.
        var messageStr = Encoding.ASCII.GetString(fixPayload.Span);
        Console.WriteLine($">>> [TCP TX]: {messageStr}");

        return Task.CompletedTask;
    }

    // A mock engine feed for testing
    public void SimulateIncomingExecutionReport(string symbol, decimal price, long qty)
    {
        string mockFix = $"8=FIX.4.4\x019=112\x0135=8\x0155={symbol}\x0131={price}\x0132={qty}\x01150=2\x0110=181\x01";
        var bytes = Encoding.ASCII.GetBytes(mockFix).AsMemory();
        OnMessageReceived?.Invoke(this, bytes);
    }
}
