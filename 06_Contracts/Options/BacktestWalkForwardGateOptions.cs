namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Configurable pass-gate thresholds for rolling walk-forward validation.
/// Section: Backtest:WalkForwardGate
/// </summary>
public sealed class BacktestWalkForwardGateOptions
{
    // Widened from 60/20/80 → 120/30/300 on 2026-04-18 to attack WF=FAIL on BTC 4h 365d.
    // Root cause: ConfidencePercentileFloor is path-dependent on a 500-sample rolling history.
    // Train folds only accumulated ~70 samples before, so the percentile threshold differed
    // sharply between train and OOS — the gate was comparing two different strategies. Doubling
    // TrainDays to 120 (~720 4h candles) and raising MinTrainCandles to 300 makes the percentile
    // estimator stable enough that train and OOS measure the same entry selectivity.
    public int TrainDays { get; set; } = 120;
    public int TestDays { get; set; } = 30;
    public int StepDays { get; set; } = 10;
    public int MinTrainCandles { get; set; } = 300;
    public int MinTestCandles { get; set; } = 30;
    public int MinWindows { get; set; } = 3;
    public double MinWalkForwardEfficiency { get; set; } = 0.30;
    public double MinOosSharpe { get; set; } = 0.10;
    public double MinPositiveOosRatio { get; set; } = 0.50;
    public int MinHorizonDaysForEnforcement { get; set; } = 180;

    /// <summary>
    /// When true (and not fast-screen), runs walk-forward parameter optimization on the full candle
    /// series before the primary replay and applies the last window's grid-search winner to the
    /// timeframe profile for that session only (baseline restored afterward).
    /// </summary>
    public bool UseWfoSelectedParamsForPrimaryBacktest { get; set; }

    /// <summary>Minimum base candles before WFO profile selection runs (avoids tiny histories).</summary>
    public int MinCandlesForWfoProfileSelection { get; set; } = 400;

    /// <summary>Window count for WFO profile selection.</summary>
    public int WfoProfileNumWindows { get; set; } = 5;

    /// <summary>Train ratio for WFO profile selection.</summary>
    public double WfoProfileTrainRatio { get; set; } = 0.7;

    /// <summary>
    /// When true, reserves the last <see cref="PboHoldoutRatio"/> of candles as a holdout set that WFO
    /// never sees. PBO runs ONLY on this holdout with params frozen from the WFO optimization set.
    /// This is the structural fix for PBO leakage: grid-searched params cannot overfit to PBO segments
    /// they never saw.
    /// </summary>
    public bool UsePboHoldout { get; set; } = true;

    /// <summary>Fraction of the tail reserved for PBO holdout (WFO trains on the leading portion).</summary>
    public double PboHoldoutRatio { get; set; } = 0.30;

    /// <summary>
    /// PBO segment partitioning: "Contiguous" keeps regime clusters intact (recommended for regime-
    /// sensitive strategies); "Interleaved" stride-samples across regimes (was the prior default and
    /// tends to deflate PBO by hiding regime overfit). Default is Contiguous.
    /// </summary>
    public string PboPartitionMode { get; set; } = "Contiguous";

    /// <summary>
    /// Minimum fraction of PBO test segments with positive Sharpe. Gates live readiness alongside PBO.
    /// </summary>
    public double MinStochasticDominance { get; set; } = 0.70;
}
