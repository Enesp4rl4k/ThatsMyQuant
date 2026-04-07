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
        payload.Should().Contain("tmq_execution_latency_average_ms");
        payload.Should().Contain("tmq_circuit_breaker_consecutive_errors");
        payload.Should().Contain("service=\"Execution.Worker\"");
    }
}
