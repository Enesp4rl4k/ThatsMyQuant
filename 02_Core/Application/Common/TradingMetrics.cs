using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ThatsMyQuant.Application.Common;

/// <summary>
/// Centralized .NET Metrics instrumentation for the trading engine.
/// Compatible with OpenTelemetry, Prometheus, and dotnet-counters out of the box.
/// </summary>
public static class TradingMetrics
{
    public static readonly string MeterName = "ThatsMyQuant.Trading";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    // --- Signal Pipeline ---
    public static readonly Counter<long> SignalsReceived =
        Meter.CreateCounter<long>("trading.signals.received", "signals", "Total signals ingested");

    public static readonly Counter<long> SignalsApproved =
        Meter.CreateCounter<long>("trading.signals.approved", "signals", "Signals that passed all validation");

    public static readonly Counter<long> SignalsRejected =
        Meter.CreateCounter<long>("trading.signals.rejected", "signals", "Signals rejected by validation or swarm");

    // --- Swarm Orchestration ---
    public static readonly Counter<long> SwarmNegotiations =
        Meter.CreateCounter<long>("trading.swarm.negotiations", "negotiations", "Total swarm negotiation rounds");

    public static readonly Counter<long> SwarmApprovals =
        Meter.CreateCounter<long>("trading.swarm.approvals", "negotiations", "Swarm-approved trades");

    public static readonly Counter<long> SwarmVetos =
        Meter.CreateCounter<long>("trading.swarm.vetos", "negotiations", "Swarm-vetoed trades");

    public static readonly Counter<long> AgentTimeouts =
        Meter.CreateCounter<long>("trading.agents.timeouts", "timeouts", "Agent step timeouts");

    public static readonly Histogram<double> SwarmLatencyMs =
        Meter.CreateHistogram<double>("trading.swarm.latency", "ms", "Swarm negotiation latency");

    // --- Trade Execution ---
    public static readonly Counter<long> TradesExecuted =
        Meter.CreateCounter<long>("trading.trades.executed", "trades", "Successfully executed trades");

    public static readonly Counter<long> TradesFailed =
        Meter.CreateCounter<long>("trading.trades.failed", "trades", "Failed trade executions");

    // --- Tick Pipeline ---
    public static readonly Counter<long> TicksIngested =
        Meter.CreateCounter<long>("trading.ticks.ingested", "ticks", "Raw ticks received from exchange");

    public static readonly Counter<long> TicksDropped =
        Meter.CreateCounter<long>("trading.ticks.dropped", "ticks", "Ticks dropped due to backpressure");

    // --- ActivitySource for distributed tracing ---
    public static readonly ActivitySource ActivitySource = new(MeterName, "1.0.0");
}
