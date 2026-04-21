namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Tunables for backtest throughput and observability. Bind section <c>Backtest:Performance</c>.
/// </summary>
/// <remarks>
/// Performance roadmap: (A) console/PBO tuning and faster PBO rank pass; (B) parallel WFO grid via per-run profile overlay;
/// (C) optional fast agent stack for backtest-only runs; (D) allocation reduction in replay/PBO hot paths.
/// </remarks>
public sealed class BacktestPerformanceOptions
{
    /// <summary>Emit periodic Console lines during main candle replay.</summary>
    public bool LogReplayProgressToConsole { get; set; } = true;

    /// <summary>How many progress points for replay (including implicit final). Min 2 ≈ start + end only.</summary>
    public int ReplayProgressSteps { get; set; } = 10;

    /// <summary>Emit PBO combination progress to Console.</summary>
    public bool LogPboProgressToConsole { get; set; } = true;

    /// <summary>
    /// Max concurrent PBO combination workers. 0 = auto: max(2, ProcessorCount - 1).
    /// Lower this on laptops or when co-located with other CPU-heavy services.
    /// </summary>
    public int PboMaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// When true, WFO train grid evaluates candidates in parallel using per-run profile overlays (no shared options mutation).
    /// </summary>
    public bool EnableParallelWfoGridSearch { get; set; } = true;

    /// <summary>
    /// Max concurrent WFO grid workers. 0 = auto: max(2, ProcessorCount - 1).
    /// </summary>
    public int WfoGridMaxDegreeOfParallelism { get; set; }
}
