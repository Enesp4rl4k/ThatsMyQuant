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
    double LastExecutionLatencyMs,
    long ExecutionReportsReceived,
    long ExecutionReportsDedupedMemory,
    long ExecutionReportsDedupedCheckpoint,
    long ExecutionReportsOutOfOrder,
    long ExecutionSyncReplayRuns,
    long ExecutionSyncReplayDriftCount,
    long InferencePoolEnginesBuilt,
    long InferencePoolTargetSize,
    long InferenceLazyFillComplete,
    long InferenceLazyPoolBackgroundFailuresTotal,
    long ExecutionSignalQueuePendingApprox,
    long VenueHttpFailuresTotal,
    long EntrySlippageSamples,
    double AverageAbsEntrySlippagePercent,
    double LastAbsEntrySlippagePercent);

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
    private readonly Histogram<double> _entrySlippageAbsPercentHistogram;
    private readonly Counter<long> _executionReportsReceivedCounter;
    private readonly Counter<long> _executionReportsDedupedMemoryCounter;
    private readonly Counter<long> _executionReportsDedupedCheckpointCounter;
    private readonly Counter<long> _executionReportsOutOfOrderCounter;
    private readonly Counter<long> _executionSyncReplayRunsCounter;
    private readonly Counter<long> _executionSyncReplayDriftCounter;
    private readonly Counter<long> _venueHttpFailuresCounter;

    private long _signalsQueued;
    private long _signalsRejected;
    private long _tradesSucceeded;
    private long _tradesFailed;
    private long _deadLetters;
    private long _lifecycleSupervisionPasses;
    private long _executionReportsReceived;
    private long _executionReportsDedupedMemory;
    private long _executionReportsDedupedCheckpoint;
    private long _executionReportsOutOfOrder;
    private long _executionSyncReplayRuns;
    private long _executionSyncReplayDriftCount;
    private long _inferencePoolEnginesBuilt;
    private long _inferencePoolTargetSize;
    private long _inferenceLazyFillComplete;
    private long _inferenceLazyPoolBackgroundFailures;
    private long _executionSignalQueuePendingApprox;
    private long _venueHttpFailures;
    private long _latencySamples;
    private double _totalExecutionLatencyMs;
    private double _lastExecutionLatencyMs;
    private long _slippageSamples;
    private double _totalAbsEntrySlippagePercent;
    private double _lastAbsEntrySlippagePercent;
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
        _entrySlippageAbsPercentHistogram = _meter.CreateHistogram<double>("trades.entry.slippage.abs.percent");
        _executionReportsReceivedCounter = _meter.CreateCounter<long>("execution.reports.received");
        _executionReportsDedupedMemoryCounter = _meter.CreateCounter<long>("execution.reports.deduped.memory");
        _executionReportsDedupedCheckpointCounter = _meter.CreateCounter<long>("execution.reports.deduped.checkpoint");
        _executionReportsOutOfOrderCounter = _meter.CreateCounter<long>("execution.reports.out.of.order");
        _executionSyncReplayRunsCounter = _meter.CreateCounter<long>("execution.sync.replay.runs");
        _executionSyncReplayDriftCounter = _meter.CreateCounter<long>("execution.sync.replay.drift");
        _venueHttpFailuresCounter = _meter.CreateCounter<long>("venue.http.errors");
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

    public void RecordExecutionReportReceived(string? symbol = null)
    {
        Interlocked.Increment(ref _executionReportsReceived);
        _executionReportsReceivedCounter.Add(1, BuildTags(symbol));
        Touch();
    }

    public void RecordExecutionReportDedupedMemory(string? symbol = null)
    {
        Interlocked.Increment(ref _executionReportsDedupedMemory);
        _executionReportsDedupedMemoryCounter.Add(1, BuildTags(symbol));
        Touch();
    }

    public void RecordExecutionReportDedupedCheckpoint(string? symbol = null)
    {
        Interlocked.Increment(ref _executionReportsDedupedCheckpoint);
        _executionReportsDedupedCheckpointCounter.Add(1, BuildTags(symbol));
        Touch();
    }

    public void RecordExecutionReportOutOfOrder(string? symbol = null)
    {
        Interlocked.Increment(ref _executionReportsOutOfOrder);
        _executionReportsOutOfOrderCounter.Add(1, BuildTags(symbol));
        Touch();
    }

    public void RecordExecutionSyncReplayRun()
    {
        Interlocked.Increment(ref _executionSyncReplayRuns);
        _executionSyncReplayRunsCounter.Add(1);
        Touch();
    }

    public void RecordExecutionSyncReplayDrift(int driftCount)
    {
        if (driftCount <= 0)
        {
            return;
        }

        Interlocked.Add(ref _executionSyncReplayDriftCount, driftCount);
        _executionSyncReplayDriftCounter.Add(driftCount);
        Touch();
    }

    /// <summary>
    /// Updates inference pool gauges (lazy warm-up progress and completion).
    /// </summary>
    public void SetInferencePoolTelemetry(int enginesBuilt, int targetPoolSize, bool lazyBackgroundFillComplete)
    {
        if (targetPoolSize < 0 || enginesBuilt < 0)
            return;
        Interlocked.Exchange(ref _inferencePoolEnginesBuilt, enginesBuilt);
        Interlocked.Exchange(ref _inferencePoolTargetSize, targetPoolSize);
        Interlocked.Exchange(ref _inferenceLazyFillComplete, lazyBackgroundFillComplete ? 1 : 0);
        Touch();
    }

    public void RecordInferenceLazyPoolBackgroundFillFailure()
    {
        Interlocked.Increment(ref _inferenceLazyPoolBackgroundFailures);
        Touch();
    }

    /// <summary>
    /// Best-effort pending count for the execution signal queue (Execution.Worker).
    /// </summary>
    public void SetExecutionSignalQueueDepth(int pendingApprox)
    {
        if (pendingApprox < 0)
            return;
        Interlocked.Exchange(ref _executionSignalQueuePendingApprox, pendingApprox);
        Touch();
    }

    /// <summary>
    /// Records absolute entry slippage in percent (signal vs filled entry). Call after a successful venue fill when <see cref="Trade.EntrySlippagePercent"/> is set.
    /// </summary>
    public void RecordEntrySlippagePercent(decimal entrySlippagePercent, string? symbol = null)
    {
        var abs = Math.Abs((double)entrySlippagePercent);
        Interlocked.Increment(ref _slippageSamples);
        AddDouble(ref _totalAbsEntrySlippagePercent, abs);
        Interlocked.Exchange(ref _lastAbsEntrySlippagePercent, abs);
        _entrySlippageAbsPercentHistogram.Record(abs, BuildTags(symbol));
        Touch();
    }

    /// <summary>
    /// Non-success HTTP responses from venue REST APIs (e.g. OKX/Binance).
    /// </summary>
    public void RecordVenueHttpFailure(string? venue = null)
    {
        Interlocked.Increment(ref _venueHttpFailures);
        _venueHttpFailuresCounter.Add(1, BuildVenueTags(venue));
        Touch();
    }

    public OperationalTelemetrySnapshot GetSnapshot()
    {
        var latencySamples = Interlocked.Read(ref _latencySamples);
        var totalLatency = Interlocked.CompareExchange(ref _totalExecutionLatencyMs, 0, 0);
        var averageLatency = latencySamples > 0
            ? totalLatency / latencySamples
            : 0d;

        var slipSamples = Interlocked.Read(ref _slippageSamples);
        var totalSlip = Interlocked.CompareExchange(ref _totalAbsEntrySlippagePercent, 0, 0);
        var avgSlip = slipSamples > 0 ? totalSlip / slipSamples : 0d;
        var lastSlip = Interlocked.CompareExchange(ref _lastAbsEntrySlippagePercent, 0, 0);

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
            Interlocked.CompareExchange(ref _lastExecutionLatencyMs, 0, 0),
            Interlocked.Read(ref _executionReportsReceived),
            Interlocked.Read(ref _executionReportsDedupedMemory),
            Interlocked.Read(ref _executionReportsDedupedCheckpoint),
            Interlocked.Read(ref _executionReportsOutOfOrder),
            Interlocked.Read(ref _executionSyncReplayRuns),
            Interlocked.Read(ref _executionSyncReplayDriftCount),
            Interlocked.Read(ref _inferencePoolEnginesBuilt),
            Interlocked.Read(ref _inferencePoolTargetSize),
            Interlocked.Read(ref _inferenceLazyFillComplete),
            Interlocked.Read(ref _inferenceLazyPoolBackgroundFailures),
            Interlocked.Read(ref _executionSignalQueuePendingApprox),
            Interlocked.Read(ref _venueHttpFailures),
            Interlocked.Read(ref _slippageSamples),
            avgSlip,
            lastSlip);
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

    private static TagList BuildVenueTags(string? venue)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(venue))
        {
            tags.Add("venue", venue);
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
