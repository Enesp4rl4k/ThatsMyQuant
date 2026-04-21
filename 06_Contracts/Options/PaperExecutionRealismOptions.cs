namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Paper-trading realism knobs. Default off to preserve backtest parity; turn on in paper sessions
/// to stress-test live friction before real capital. Section: Paper:Realism.
/// </summary>
public sealed class PaperExecutionRealismOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>Simulated median latency between decision and fill. Recorded on the position.</summary>
    public int MedianLatencyMs { get; set; } = 120;

    /// <summary>Extra latency added in Volatile regime (queue depth churn, engine throttle).</summary>
    public int VolatileLatencyPenaltyMs { get; set; } = 250;

    /// <summary>Order rejection probability in Ranging/Trending regime. 0..1.</summary>
    public double BaseRejectionProbability { get; set; } = 0.005;

    /// <summary>Order rejection probability in Volatile regime (margin checks, stale-mid rejects).</summary>
    public double VolatileRejectionProbability { get; set; } = 0.025;

    /// <summary>RNG seed for deterministic replay. 0 = time-seeded.</summary>
    public int RandomSeed { get; set; } = 0;
}
