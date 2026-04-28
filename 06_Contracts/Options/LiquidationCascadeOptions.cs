namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// [P1.1] Configuration for the live LiquidationCascadeFader hosted service.
/// Triggers and dominance thresholds map directly to
/// <see cref="ThatsMyQuant.Application.Strategy.LiquidationCascadeFader.Detect"/>.
/// </summary>
public sealed class LiquidationCascadeOptions
{
    public const string SectionName = "LiquidationCascade";

    /// <summary>Master switch for the hosted service. Default false (opt-in).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Rolling aggregation window length in seconds. Default 300 (5 min).</summary>
    public int WindowSeconds { get; set; } = 300;

    /// <summary>
    /// USD trigger notional for BTC-family perps (BTC-USDT-SWAP, BTC-USD-SWAP).
    /// NOTE: notional in the hosted service is computed as sz × bkPx WITHOUT ctVal,
    /// so this threshold must be calibrated empirically against the live feed.
    /// Default 5_000_000 — matches LiquidationCascadeFader.DefaultBtcTriggerNotional
    /// for ~$5M of BTC contract size in 5 min.
    /// </summary>
    public decimal BtcTriggerNotional { get; set; } = 5_000_000m;

    /// <summary>USD trigger notional for non-BTC alts. Default 1_000_000.</summary>
    public decimal AltTriggerNotional { get; set; } = 1_000_000m;

    /// <summary>Required share of dominant side (0.5, 1.0]. Default 0.70.</summary>
    public double DominanceRatio { get; set; } = 0.70;

    /// <summary>
    /// Minimum strength score (0-100) required to actually fire a fade signal.
    /// Default 60 — filters out borderline cascades that just barely cross trigger.
    /// </summary>
    public int MinStrengthScore { get; set; } = 60;

    /// <summary>
    /// Cooldown between fade signals on the same symbol (seconds). Avoids re-arming
    /// during the tail of a multi-cluster cascade. Default 600 (10 min).
    /// </summary>
    public int PerSymbolCooldownSeconds { get; set; } = 600;

    /// <summary>
    /// Max number of liquidation events retained per symbol in the rolling window
    /// (memory cap). Default 10_000.
    /// </summary>
    public int MaxEventsPerSymbol { get; set; } = 10_000;

    /// <summary>
    /// When true, detected cascades dispatch an ExecuteTradeCommand. When false,
    /// the service only logs detections (shadow / observation mode). Default true.
    /// </summary>
    public bool DispatchSignals { get; set; } = true;
}
