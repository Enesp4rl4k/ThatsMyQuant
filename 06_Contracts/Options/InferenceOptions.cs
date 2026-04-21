namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Controls ML.NET prediction engine pool sizing and warm-up behavior for InferenceAgent.
/// </summary>
public sealed class InferenceOptions
{
    /// <summary>
    /// Exact pool size when &gt; 0. When 0, pool size follows logical processor count (subject to <see cref="MaxPredictionEngines"/>).
    /// </summary>
    public int PredictionEngineCount { get; set; }

    /// <summary>
    /// When &gt; 0, caps the pool when <see cref="PredictionEngineCount"/> is 0 (i.e. use min(ProcessorCount, this)).
    /// When 0, no cap (still clamped to 128 by <see cref="GetEffectivePoolSize"/>).
    /// </summary>
    public int MaxPredictionEngines { get; set; }

    /// <summary>
    /// When true (default), warm-up uses <c>Parallel.For</c>. When false, sequential loop (lower CPU spike, slower wall time).
    /// </summary>
    public bool UseParallelWarmup { get; set; } = true;

    /// <summary>
    /// When true (default), only one prediction engine is created before the first inference; remaining engines are filled in a background task after first use.
    /// When false, the full pool is built during construction (higher startup cost, pool ready immediately).
    /// </summary>
    public bool LazyPoolWarmup { get; set; } = true;

    /// <summary>
    /// Minimum <c>ILogger</c> level for lazy pool background fill failures (e.g. <c>Warning</c>, <c>Error</c>). Invalid or empty values default to <c>Warning</c>. Use <c>None</c> to suppress failure logs.
    /// </summary>
    public string LazyPoolBackgroundLogLevel { get; set; } = "Warning";

    /// <summary>
    /// Effective number of ML.NET prediction engine instances in the pool (and warm-up iterations).
    /// </summary>
    public int GetEffectivePoolSize()
    {
        if (PredictionEngineCount > 0)
            return Math.Clamp(PredictionEngineCount, 1, 128);

        var n = Environment.ProcessorCount;
        if (MaxPredictionEngines > 0)
            n = Math.Min(n, MaxPredictionEngines);
        return Math.Clamp(n, 1, 128);
    }
}
