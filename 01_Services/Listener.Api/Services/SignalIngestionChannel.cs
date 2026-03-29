using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api.Services;

/// <summary>
/// Intercepts inbound external JSON payloads instantly, shielding the HTTP web container from the heavy AI processing Swarm.
/// </summary>
public class SignalIngestionChannel
{
    private readonly Channel<Signal> _channel;

    public SignalIngestionChannel()
    {
        // Prevent Webhook DoS attacks by bounding the RAM ingest limit
        var options = new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<Signal>(options);
    }

    public bool TryWrite(Signal signal) => _channel.Writer.TryWrite(signal);
    
    public IAsyncEnumerable<Signal> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
