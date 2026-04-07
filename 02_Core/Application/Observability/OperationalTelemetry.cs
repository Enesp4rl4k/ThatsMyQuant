using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace ThatsMyQuant.Application.Observability;

public sealed record OperationalTelemetrySnapshot(
    string ServiceName,
    DateTime LastUpdatedUtc,
    long SignalsQueued,
    long SignalsRejected,
    long TradesSucceeded,
    long TradesFailed,
    long DeadLetters,
    long LifecycleSupervisionPasses,
    double AverageExecutionLatencyMs,
    double LastExecutionLatencyMs);

public sealed class OperationalTelemetry : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _signalsQueuedCounter;
    private readonly Counter<long> _signalsRejectedCounter;
    private readonly Counter<long> _tradesSucceededCounter;
    private readonly Counter<long> _tradesFailedCounter;
    private readonly Counter<long> _deadLettersCounter;
    private readonly Counter<long> _lifecycleSupervisionCounter;
    private readonly Histogram<double> _executionLatencyHistogram;

    private long _signalsQueued;
    private long _signalsRejected;
    private long _tradesSucceeded;
    private long _tradesFailed;
    private long _deadLetters;
    private long _lifecycleSupervisionPasses;
    private long _latencySamples;
    private double _totalExecutionLatencyMs;
    private double _lastExecutionLatencyMs;
    private long _lastUpdatedTicks;

    public OperationalTelemetry(string serviceName = "ThatsMyQuant")
    {
        ServiceName = serviceName;
        _meter = new Meter($"ThatsMyQuant.{serviceName.Replace(' ', '.').Replace('-', '.')}", "1.0.0");
        _signalsQueuedCounter = _meter.CreateCounter<long>("signals.queued");
        _signalsRejectedCounter = _meter.CreateCounter<long>("signals.rejected");
        _tradesSucceededCounter = _meter.CreateCounter<long>("trades.succeeded");
        _tradesFailedCounter = _meter.CreateCounter<long>("trades.failed");
        _deadLettersCounter = _meter.CreateCounter<long>("signals.deadletter");
        _lifecycleSupervisionCounter = _meter.CreateCounter<long>("positions.supervised");
        _executionLatencyHistogram = _meter.CreateHistogram<double>("trades.execution.latency.ms");
        _lastUpdatedTicks = DateTime.UtcNow.Ticks;
    }

    public string ServiceName { get; }

    public void RecordSignalQueued(string? symbol = null)
    {
        Interlocked.Increment(ref _signalsQueued);
        _signalsQueuedCounter.Add(1, BuildTags(symbol));
        Touch();
    }

    public void RecordSignalRejected(string? symbol = null)
    {
        Interlocked.Increment(ref _signalsRejected);
        _signalsRejectedCounter.Add(1, BuildTags(symbol));
        Touch();
    }

    public void RecordTradeSucceeded(long latencyMs, string? symbol = null)
    {
        Interlocked.Increment(ref _tradesSucceeded);
        _tradesSucceededCounter.Add(1, BuildTags(symbol));
        _executionLatencyHistogram.Record(latencyMs, BuildTags(symbol));
        Interlocked.Increment(ref _latencySamples);
        AddDouble(ref _totalExecutionLatencyMs, latencyMs);
        Interlocked.Exchange(ref _lastExecutionLatencyMs, latencyMs);
        Touch();
    }

    public void RecordTradeFailed(string? symbol = null)
    {
        Interlocked.Increment(ref _tradesFailed);
        _tradesFailedCounter.Add(1, BuildTags(symbol));
        Touch();
    }

    public void RecordDeadLetter(string? symbol = null)
    {
        Interlocked.Increment(ref _deadLetters);
        _deadLettersCounter.Add(1, BuildTags(symbol));
        Touch();
    }

    public void RecordLifecycleSupervision(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _lifecycleSupervisionPasses, count);
        _lifecycleSupervisionCounter.Add(count);
        Touch();
    }

    public OperationalTelemetrySnapshot GetSnapshot()
    {
        var latencySamples = Interlocked.Read(ref _latencySamples);
        var totalLatency = Interlocked.CompareExchange(ref _totalExecutionLatencyMs, 0, 0);
        var averageLatency = latencySamples > 0
            ? totalLatency / latencySamples
            : 0d;

        return new OperationalTelemetrySnapshot(
            ServiceName,
            new DateTime(Interlocked.Read(ref _lastUpdatedTicks), DateTimeKind.Utc),
            Interlocked.Read(ref _signalsQueued),
            Interlocked.Read(ref _signalsRejected),
            Interlocked.Read(ref _tradesSucceeded),
            Interlocked.Read(ref _tradesFailed),
            Interlocked.Read(ref _deadLetters),
            Interlocked.Read(ref _lifecycleSupervisionPasses),
            averageLatency,
            Interlocked.CompareExchange(ref _lastExecutionLatencyMs, 0, 0));
    }

    public void Dispose() => _meter.Dispose();

    private static TagList BuildTags(string? symbol)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            tags.Add("symbol", symbol);
        }

        return tags;
    }

    private void Touch()
        => Interlocked.Exchange(ref _lastUpdatedTicks, DateTime.UtcNow.Ticks);

    private static void AddDouble(ref double target, double value)
    {
        double initialValue;
        double computedValue;

        do
        {
            initialValue = target;
            computedValue = initialValue + value;
        }
        while (Math.Abs(Interlocked.CompareExchange(ref target, computedValue, initialValue) - initialValue) > double.Epsilon);
    }
}
