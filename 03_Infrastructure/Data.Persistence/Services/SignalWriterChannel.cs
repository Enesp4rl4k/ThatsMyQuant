using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// A non-blocking asynchronous RAM pipeline protecting the execution threads from SQL Server Latency.
/// Uses DropOldest bounding to defend the ecosystem from OOM memory corruption during hyper-volatile surges.
/// </summary>
public class SignalWriterChannel
{
    private readonly Channel<Signal> _channel;

    public SignalWriterChannel()
    {
        // 50,000 tick barrier. Defends the application's Heap topology if the database connection crashes.
        var options = new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        };
        _channel = Channel.CreateBounded<Signal>(options);
    }

    /// <summary>Instantly deposits physical pointers into the bounded buffer without waiting for SQL I/O.</summary>
    public bool TryWrite(Signal signal) => _channel.Writer.TryWrite(signal);
    
    public bool TryRead(out Signal signal) => _channel.Reader.TryRead(out signal!);

    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) => _channel.Reader.WaitToReadAsync(ct);
}
