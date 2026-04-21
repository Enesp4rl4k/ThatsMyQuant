using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using Xunit;

namespace ThatsMyQuant.Tests.Observability;

public class PrometheusMetricsFormatterTests
{
    [Fact]
    public void Format_IncludesRuntimeTelemetryAndCircuitBreakerMetrics()
    {
        var runtimeState = new ServiceRuntimeState("Execution.Worker");
        runtimeState.MarkReady();

        var telemetry = new OperationalTelemetry("Execution.Worker");
        telemetry.RecordSignalQueued("BTCUSDT");
        telemetry.RecordTradeSucceeded(42, "BTCUSDT");
        telemetry.RecordExecutionReportReceived("BTCUSDT");
        telemetry.RecordExecutionReportDedupedMemory("BTCUSDT");
        telemetry.RecordExecutionReportDedupedCheckpoint("BTCUSDT");
        telemetry.RecordExecutionReportOutOfOrder("BTCUSDT");
        telemetry.RecordExecutionSyncReplayRun();
        telemetry.RecordExecutionSyncReplayDrift(2);
        telemetry.SetInferencePoolTelemetry(4, 8, false);
        telemetry.RecordInferenceLazyPoolBackgroundFillFailure();
        telemetry.SetExecutionSignalQueueDepth(3);
        telemetry.RecordVenueHttpFailure("okx");
        telemetry.RecordEntrySlippagePercent(0.25m, "BTCUSDT");

        var portfolio = Substitute.For<IPortfolioManager>();
        portfolio.GetDrawdownPercent().Returns(2.5m);
        var circuitBreaker = new CircuitBreakerService(
            NullLogger<CircuitBreakerService>.Instance,
            portfolio,
            maxDrawdownPercent: 5m,
            maxConsecutiveErrors: 3,
            maxOrdersPerSecond: 10);
        circuitBreaker.RecordError(new InvalidOperationException("simulated"));

        var payload = PrometheusMetricsFormatter.Format(runtimeState, telemetry, circuitBreaker.GetStatus());

        payload.Should().Contain("tmq_runtime_ready");
        payload.Should().Contain("tmq_signals_queued_total");
        payload.Should().Contain("tmq_trades_succeeded_total");
        payload.Should().Contain("tmq_execution_reports_received_total");
        payload.Should().Contain("tmq_execution_reports_out_of_order_total");
        payload.Should().Contain("tmq_execution_sync_replay_runs_total");
        payload.Should().Contain("tmq_execution_sync_replay_drift_count");
        payload.Should().Contain("tmq_execution_latency_average_ms");
        payload.Should().Contain("tmq_circuit_breaker_consecutive_errors");
        payload.Should().Contain("service=\"Execution.Worker\"");
        payload.Should().Contain("tmq_inference_pool_engines_built");
        payload.Should().Contain("tmq_inference_pool_target_size");
        payload.Should().Contain("tmq_inference_lazy_fill_complete");
        payload.Should().Contain("tmq_inference_lazy_pool_background_failures_total");
        payload.Should().Contain("tmq_execution_signal_queue_pending_approx");
        payload.Should().Contain("tmq_venue_http_errors_total");
        payload.Should().Contain("tmq_entry_slippage_observations_total");
        payload.Should().Contain("tmq_entry_slippage_abs_percent_average");
        payload.Should().Contain("tmq_entry_slippage_abs_percent_last");
    }
}
