using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// A bounded multi-producer write buffer that decouples signal persistence from the request hot path.
/// Callers can fall back to direct persistence if the buffer is saturated.
/// </summary>
public class SignalWriterChannel
{
    private readonly Channel<Signal> _channel;

    public SignalWriterChannel()
    {
        // Large enough for volatility bursts while still giving callers a clear backpressure signal.
        var options = new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        };
        _channel = Channel.CreateBounded<Signal>(options);
    }

    /// <summary>Instantly deposits physical pointers into the bounded buffer without waiting for SQL I/O.</summary>
    public bool TryWrite(Signal signal) => _channel.Writer.TryWrite(signal);
    
    public bool TryRead(out Signal signal) => _channel.Reader.TryRead(out signal!);

    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) => _channel.Reader.WaitToReadAsync(ct);
}
