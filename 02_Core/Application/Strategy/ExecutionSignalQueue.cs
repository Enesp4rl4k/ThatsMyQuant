using System.Collections.Concurrent;
using System.Threading.Channels;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public sealed class ExecutionSignalQueue
{
    private readonly ConcurrentDictionary<Guid, byte> _pendingSignals = new();
    private readonly Channel<Signal> _channel = Channel.CreateBounded<Signal>(new BoundedChannelOptions(512)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public bool TryEnqueue(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (!_pendingSignals.TryAdd(signal.Id, 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(Clone(signal)))
        {
            return true;
        }

        _pendingSignals.TryRemove(signal.Id, out _);
        return false;
    }

    public async ValueTask EnqueueAsync(Signal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (!_pendingSignals.TryAdd(signal.Id, 0))
        {
            return;
        }

        try
        {
            await _channel.Writer.WriteAsync(Clone(signal), cancellationToken);
        }
        catch
        {
            _pendingSignals.TryRemove(signal.Id, out _);
            throw;
        }
    }

    public IAsyncEnumerable<Signal> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Signals accepted but not yet completed (still in-flight in the channel or pending map).
    /// </summary>
    public int PendingCount => _pendingSignals.Count;

    public void MarkCompleted(Guid signalId)
        => _pendingSignals.TryRemove(signalId, out _);

    private static Signal Clone(Signal signal)
    {
        return new Signal
        {
            Id = signal.Id,
            Source = signal.Source,
            Symbol = signal.Symbol,
            Side = signal.Side,
            Price = signal.Price,
            Status = signal.Status,
            RetryCount = signal.RetryCount,
            Quantity = signal.Quantity,
            Confidence = signal.Confidence,
            Reasoning = signal.Reasoning,
            StrategyTag = signal.StrategyTag,
            RegimeTag = signal.RegimeTag,
            MarketType = signal.MarketType,
            Exchange = signal.Exchange,
            Timestamp = signal.Timestamp,
            ClaimedAtUtc = signal.ClaimedAtUtc,
            ClaimedBy = signal.ClaimedBy,
            Volume24h = signal.Volume24h,
            OrderFlowImbalance = signal.OrderFlowImbalance,
            BestBid = signal.BestBid,
            BestAsk = signal.BestAsk,
            WallAgeMs = signal.WallAgeMs,
            ExecutionAction = signal.ExecutionAction,
            PriceActionSignal = signal.PriceActionSignal,
            ReasonFlags = signal.ReasonFlags,
            Metadata = signal.Metadata.Count == 0
                ? []
                : signal.Metadata.ToDictionary(entry => entry.Key, entry => entry.Value)
        };
    }
}
