using System;

namespace ThatsMyQuant.Application.Diagnostics;

/// <summary>
/// Decision verdict for a single per-iteration signal evaluation. Drives the
/// colour cue + filter on the Signal Diagnostics page.
/// </summary>
/// <remarks>
/// "Suppressed*" values mean the runner emitted a fire-able signal but a
/// downstream gate killed it before <see cref="HandleOpenAsync"/> ran.
/// "Skipped*" means the runner itself declined to emit a signal (gates inside
/// the backtest runner: velocity filter, regime gate, spread floor, etc.).
/// </remarks>
public enum SignalDecision
{
    BelowThreshold,
    NearTrigger,
    WouldFire,
    Fired,
    HoldOpen,
    SuppressedByCircuitBreakerTier,
    SuppressedByCircuitBreakerGlobal,
    SuppressedByMacroPause,
    SuppressedByOrphanHalt,
    SuppressedByBlacklist,
    SuppressedByPortfolioRisk,
    SuppressedByKillSwitch,
    SkippedByRunner,
    Flat
}

/// <summary>
/// Immutable snapshot of one strategy × symbol/pair signal evaluation. Published
/// by executors on every iteration (typically once per bar close), held in
/// <see cref="ISignalDiagnosticsRegistry"/>, and broadcast to the web via
/// <c>SignalDiagnosticsHub</c>.
/// </summary>
/// <param name="UtcTimestamp">When the evaluation happened. Used by the UI to
/// render "updated 4s ago" and to detect stale strategies (no recent publish).</param>
/// <param name="Tier">Logical strategy tier, e.g. "Funding:Long",
/// "Pairs:Long", "EdgeLab:single_asset". Drives the Tier filter dropdown.</param>
/// <param name="StrategyTag">Specific strategy identifier within the tier.</param>
/// <param name="Key">Symbol ("LTCUSDT") for single-leg strategies, or pair
/// ("ADAUSDT-XRPUSDT") for pairs. Used as the row identity in the registry.</param>
/// <param name="CurrentValue">The evaluated signal value: funding rate (decimal,
/// e.g. 0.00012 = 1.2bp), spread z-score, target weight, etc. Sign preserved.</param>
/// <param name="EntryThreshold">The (positive) threshold the absolute value of
/// <see cref="CurrentValue"/> must cross to fire. Both Funding and Pairs use
/// symmetric thresholds (long fires above +T, short below -T).</param>
/// <param name="ExitThreshold">Optional exit threshold for diagnostic display.</param>
/// <param name="StopThreshold">Optional stop-loss threshold for diagnostic display.</param>
/// <param name="DistanceToTriggerPct">|current| / |threshold| × 100. 100 means
/// at the trigger; >100 means past it; below 100 means we're nowhere close.
/// UI renders as a horizontal bar capped at 150.</param>
/// <param name="Decision">The verdict for this iteration. See <see cref="SignalDecision"/>.</param>
/// <param name="SkipOrSuppressReason">Free-form reason string when the decision
/// is a Skipped* or Suppressed* variant. Already-formatted, ready for display
/// (e.g. "VelocityFilter: 0.42 &lt; 0.50", "MacroPause:FOMC T-15m").</param>
/// <param name="RegimeContext">Optional regime label for funding-style strategies
/// ("Volatile" / "Trending" / "Ranging" / "Unknown"). Null for pairs/EdgeLab.</param>
/// <param name="HasOpenPosition">Whether the executor currently holds a live
/// position in this key. Drives the "open" badge on the row.</param>
/// <param name="EstimatedNotionalIfFired">Approximate USD notional that would
/// have been ordered if the signal fired right now (post-regime sizing). Useful
/// to gauge "would this even matter if it triggered". Null when not applicable
/// (e.g. EdgeLab below MinDeltaUsd).</param>
public sealed record SignalDiagnosticSnapshot(
    DateTime UtcTimestamp,
    string   Tier,
    string   StrategyTag,
    string   Key,
    decimal  CurrentValue,
    decimal  EntryThreshold,
    decimal? ExitThreshold,
    decimal? StopThreshold,
    decimal  DistanceToTriggerPct,
    SignalDecision Decision,
    string?  SkipOrSuppressReason,
    string?  RegimeContext,
    bool     HasOpenPosition,
    decimal? EstimatedNotionalIfFired);
