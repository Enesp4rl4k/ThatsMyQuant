using System.Text;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Application.Observability;

public static class PrometheusMetricsFormatter
{
    public static string Format(
        ServiceRuntimeState runtimeState,
        OperationalTelemetry telemetry,
        CircuitBreakerStatus? circuitBreakerStatus = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeState);
        ArgumentNullException.ThrowIfNull(telemetry);

        var runtime = runtimeState.GetSnapshot();
        var snapshot = telemetry.GetSnapshot();
        var labels = $"service=\"{EscapeLabel(runtime.ServiceName)}\"";

        var builder = new StringBuilder();

        AppendGauge(builder, "tmq_runtime_ready", "Runtime readiness state (1=ready, 0=not ready)", labels, runtime.IsReady ? 1 : 0);
        AppendGauge(builder, "tmq_runtime_started_unix", "Runtime start timestamp in unix seconds", labels, new DateTimeOffset(runtime.StartedUtc).ToUnixTimeSeconds());
        AppendGauge(builder, "tmq_runtime_last_updated_unix", "Runtime last update timestamp in unix seconds", labels, new DateTimeOffset(runtime.LastUpdatedUtc).ToUnixTimeSeconds());

        AppendCounter(builder, "tmq_signals_queued_total", "Signals queued for execution", labels, snapshot.SignalsQueued);
        AppendCounter(builder, "tmq_signals_rejected_total", "Signals rejected before execution", labels, snapshot.SignalsRejected);
        AppendCounter(builder, "tmq_trades_succeeded_total", "Trades executed successfully", labels, snapshot.TradesSucceeded);
        AppendCounter(builder, "tmq_trades_failed_total", "Trades that failed during execution", labels, snapshot.TradesFailed);
        AppendCounter(builder, "tmq_signals_deadletter_total", "Signals moved to dead-letter", labels, snapshot.DeadLetters);
        AppendCounter(builder, "tmq_positions_supervised_total", "Lifecycle supervision passes", labels, snapshot.LifecycleSupervisionPasses);
        AppendCounter(builder, "tmq_execution_reports_received_total", "Execution reports received from venue stream", labels, snapshot.ExecutionReportsReceived);
        AppendCounter(builder, "tmq_execution_reports_deduped_memory_total", "Execution reports suppressed by in-memory dedupe", labels, snapshot.ExecutionReportsDedupedMemory);
        AppendCounter(builder, "tmq_execution_reports_deduped_checkpoint_total", "Execution reports suppressed by checkpoint dedupe", labels, snapshot.ExecutionReportsDedupedCheckpoint);
        AppendCounter(builder, "tmq_execution_reports_out_of_order_total", "Execution reports suppressed as out-of-order regressions", labels, snapshot.ExecutionReportsOutOfOrder);
        AppendCounter(builder, "tmq_execution_sync_replay_runs_total", "Execution sync reconciliation replay runs", labels, snapshot.ExecutionSyncReplayRuns);
        AppendCounter(builder, "tmq_execution_sync_replay_drift_count", "Drift findings from reconciliation replays", labels, snapshot.ExecutionSyncReplayDriftCount);
        AppendGauge(builder, "tmq_execution_latency_average_ms", "Average execution latency in milliseconds", labels, snapshot.AverageExecutionLatencyMs);
        AppendGauge(builder, "tmq_execution_latency_last_ms", "Most recent execution latency in milliseconds", labels, snapshot.LastExecutionLatencyMs);
        AppendGauge(builder, "tmq_execution_signal_queue_pending_approx", "Approximate pending execution signals (in-flight)", labels, snapshot.ExecutionSignalQueuePendingApprox);
        AppendCounter(builder, "tmq_venue_http_errors_total", "Non-success HTTP responses from venue REST APIs", labels, snapshot.VenueHttpFailuresTotal);
        AppendCounter(builder, "tmq_entry_slippage_observations_total", "Entry slippage samples recorded (|signal price vs fill|)", labels, snapshot.EntrySlippageSamples);
        AppendGauge(builder, "tmq_entry_slippage_abs_percent_average", "Average absolute entry slippage percent across recorded fills", labels, snapshot.AverageAbsEntrySlippagePercent);
        AppendGauge(builder, "tmq_entry_slippage_abs_percent_last", "Most recent absolute entry slippage percent", labels, snapshot.LastAbsEntrySlippagePercent);

        if (snapshot.InferencePoolTargetSize > 0 || snapshot.InferenceLazyPoolBackgroundFailuresTotal > 0)
        {
            AppendGauge(builder, "tmq_inference_pool_engines_built", "ML.NET prediction engines built for InferenceAgent pool", labels, snapshot.InferencePoolEnginesBuilt);
            AppendGauge(builder, "tmq_inference_pool_target_size", "Target InferenceAgent pool size", labels, snapshot.InferencePoolTargetSize);
            AppendGauge(builder, "tmq_inference_lazy_fill_complete", "Lazy pool background fill finished (1=yes, 0=no)", labels, snapshot.InferenceLazyFillComplete);
            AppendCounter(builder, "tmq_inference_lazy_pool_background_failures_total", "Failures during lazy pool background expansion", labels, snapshot.InferenceLazyPoolBackgroundFailuresTotal);
        }

        if (circuitBreakerStatus is not null)
        {
            AppendGauge(builder, "tmq_circuit_breaker_halted", "Circuit breaker halt state (1=halted, 0=clear)", labels, circuitBreakerStatus.IsHalted ? 1 : 0);
            AppendGauge(builder, "tmq_circuit_breaker_drawdown_percent", "Current drawdown percent observed by the circuit breaker", labels, (double)circuitBreakerStatus.CurrentDrawdownPercent);
            AppendCounter(builder, "tmq_circuit_breaker_orders_blocked_total", "Orders blocked by the circuit breaker", labels, circuitBreakerStatus.TotalOrdersBlocked);
            AppendCounter(builder, "tmq_circuit_breaker_rate_limit_hits_total", "Rate-limit hits observed by the circuit breaker", labels, circuitBreakerStatus.TotalRateLimitHits);
            AppendGauge(builder, "tmq_circuit_breaker_consecutive_errors", "Consecutive execution errors tracked by the circuit breaker", labels, circuitBreakerStatus.ConsecutiveErrors);
        }

        return builder.ToString();
    }

    private static void AppendCounter(StringBuilder builder, string name, string help, string labels, double value)
    {
        builder.AppendLine($"# HELP {name} {help}");
        builder.AppendLine($"# TYPE {name} counter");
        builder.AppendLine($"{name}{{{labels}}} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static void AppendGauge(StringBuilder builder, string name, string help, string labels, double value)
    {
        builder.AppendLine($"# HELP {name} {help}");
        builder.AppendLine($"# TYPE {name} gauge");
        builder.AppendLine($"{name}{{{labels}}} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static string EscapeLabel(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
