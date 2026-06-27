using System.Text.Json.Serialization;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Wire-format POCO for EdgeLab's <c>signals/&lt;strategy&gt;/latest.json</c> contract
/// (Quantlab repo, signals/README.md schema_version=1). Field names match the
/// snake_case keys the producer emits; deserialised via System.Text.Json with
/// <see cref="JsonPropertyNameAttribute"/>.
///
/// Nullable on every field so we can distinguish "missing key" (validation failure)
/// from "present but wrong value". <see cref="EdgeLabSignalReader"/> performs the
/// full contract check; this type is just a transport.
/// </summary>
public sealed class EdgeLabSignalDocument
{
    [JsonPropertyName("schema_version")]
    public int? SchemaVersion { get; set; }

    [JsonPropertyName("strategy")]
    public string? Strategy { get; set; }

    [JsonPropertyName("generated_at")]
    [JsonConverter(typeof(LenientDateTimeOffsetConverter))]
    public DateTimeOffset? GeneratedAt { get; set; }

    [JsonPropertyName("as_of_bar")]
    [JsonConverter(typeof(LenientDateTimeOffsetConverter))]
    public DateTimeOffset? AsOfBar { get; set; }

    [JsonPropertyName("target_weights")]
    public Dictionary<string, double>? TargetWeights { get; set; }

    [JsonPropertyName("factor_values")]
    public Dictionary<string, double>? FactorValues { get; set; }

    [JsonPropertyName("ranks")]
    public Dictionary<string, int>? Ranks { get; set; }

    [JsonPropertyName("metadata")]
    public EdgeLabSignalMetadata? Metadata { get; set; }
}

public sealed class EdgeLabSignalMetadata
{
    [JsonPropertyName("factor")] public string? Factor { get; set; }
    [JsonPropertyName("timeframe")] public string? Timeframe { get; set; }
    [JsonPropertyName("k")] public int? K { get; set; }
    [JsonPropertyName("reverse")] public bool? Reverse { get; set; }
    [JsonPropertyName("fee_bps_round_trip")] public double? FeeBpsRoundTrip { get; set; }
    [JsonPropertyName("fee_venue")] public string? FeeVenue { get; set; }
    [JsonPropertyName("fee_tier")] public string? FeeTier { get; set; }
    [JsonPropertyName("fee_style")] public string? FeeStyle { get; set; }
    [JsonPropertyName("n_symbols_total")] public int? NSymbolsTotal { get; set; }
    [JsonPropertyName("n_symbols_loaded")] public int? NSymbolsLoaded { get; set; }
    [JsonPropertyName("n_symbols_eligible_at_bar")] public int? NSymbolsEligibleAtBar { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    /// <summary>How many timeframe bars between rebalances (1 = every bar — default).
    /// EdgeLab's <c>realized_vol_4h_xs_reverse_reb6</c> emits 6 here (daily rebalance from
    /// 4h candles). Drives the freshness window: <c>max_age = 2 × rebalance_every × timeframe</c>.
    /// Missing/null treated as 1 (back-compat with v1 producers that don't set it).
    /// Added 2026-05-03 alongside the daily-reb variant; signal schema_version stays at 1
    /// because the field is purely additive — existing readers that ignore it continue to
    /// validate correctly against reb=1 strategies.</summary>
    [JsonPropertyName("rebalance_every")] public int? RebalanceEvery { get; set; }

    /// <summary>Discriminator added 2026-05-08 for the $2K cycle. Quantlab side emits
    /// <c>"single_asset"</c> for strategies that target one symbol (directional, not
    /// beta-neutral). XS strategies leave this null/absent (back-compat). Used by
    /// <c>EdgeLabSingleAssetHostedService</c> to route appropriately and skip the
    /// XS-specific 200% gross / beta-neutral validation.</summary>
    [JsonPropertyName("strategy_kind")] public string? StrategyKind { get; set; }

    /// <summary>Leverage multiplier for single-asset strategies. Default 1.0 (unlevered).
    /// Multi-symbol XS strategies don't use this; gross is implicit from sum |target_weights|.</summary>
    [JsonPropertyName("leverage")] public double? Leverage { get; set; }

    // ── [Sprint 6 — 2026-05-10] Quantlab audit contract (TMQ_INTEGRATION_CONTRACT.md) ──
    // Backtest expectation snapshots emitted with every signal. TMQ uses these to:
    //   1. Compute realized-vs-expected divergence rolling per strategy.
    //   2. Trigger half-Kelly demotion when realized 30-cyc Sharpe < kill_switch_*_30cyc.
    //   3. Hard kill (Weight=0) when realized 30-cyc Sharpe < kill_switch_*_hard.
    // All nullable: missing on legacy strategies; gracefully degrade when absent.

    /// <summary>Backtest annualised Sharpe from Quantlab's most recent paranoid pass.</summary>
    [JsonPropertyName("expected_sharpe_ann")] public double? ExpectedSharpeAnn { get; set; }

    /// <summary>Backtest mean per-cycle return in percent (e.g. 3.0 = +3%).</summary>
    [JsonPropertyName("expected_mean_pct")] public double? ExpectedMeanPct { get; set; }

    /// <summary>Backtest per-cycle return std-dev in percent.</summary>
    [JsonPropertyName("expected_std_pct")] public double? ExpectedStdPct { get; set; }

    /// <summary>Expected cycles per year (for Sharpe annualisation comparability).</summary>
    [JsonPropertyName("expected_cyc_per_yr")] public double? ExpectedCycPerYr { get; set; }

    /// <summary>Expected win-rate fraction (0..1).</summary>
    [JsonPropertyName("expected_win_rate")] public double? ExpectedWinRate { get; set; }

    /// <summary>Backtest sample size (n cycles) used to compute the expected stats.</summary>
    [JsonPropertyName("backtest_n_cycles")] public int? BacktestNCycles { get; set; }

    /// <summary>Free-form stress-test summary, e.g. "paranoid_7/8_robust_4/4".</summary>
    [JsonPropertyName("stress_test_score")] public string? StressTestScore { get; set; }

    /// <summary>Quantlab-side hint for the TMQ promotion gate.
    /// TMQ's per-strategy <c>MinPaperCyclesForLive</c> (in appsettings) takes precedence
    /// when set; this field is the "if you didn't override, use this" default.</summary>
    [JsonPropertyName("min_paper_cycles_for_live")] public int? MinPaperCyclesForLive { get; set; }

    /// <summary>Realized rolling-30-cycle Sharpe below which TMQ demotes the strategy
    /// to half-Kelly (Weight × 0.5). Default contract value -0.3.</summary>
    [JsonPropertyName("kill_switch_realized_sharpe_30cyc")] public double? KillSwitchRealizedSharpe30c { get; set; }

    /// <summary>Realized rolling-30-cycle Sharpe below which TMQ hard-kills the
    /// strategy (Weight=0, forced dry-run). Default contract value -1.0.</summary>
    [JsonPropertyName("kill_switch_realized_sharpe_hard")] public double? KillSwitchRealizedSharpeHard { get; set; }

    // ── [2026-05-14 Quantlab paused-edge mechanism] ──
    // Quantlab side identified 7 edges with realized Sharpe < 0.5× backtest over 1y.
    // Until reactivation criteria are met (60-cyc Sharpe > expected × 1.0), Quantlab's
    // emit script forces target_weights to 0 AND sets metadata.paused=true. TMQ bot
    // MUST honour this flag (skip cycle) so paused strategies don't pollute logs,
    // dashboards, or attempt zero-weight rebalances. Spec: Quantlab/integration/TMQ_PAUSED_EDGES_SPEC.md.

    /// <summary>True when Quantlab has placed this edge in paper-only/paused mode
    /// (realized Sharpe degraded vs backtest expectation). When set, TMQ bot skips
    /// the whole cycle for this strategy: no order, no rebalance, no log spam.
    /// Reactivation: Quantlab removes the edge from _PAUSED_EDGES.json → next emit
    /// has paused=false → TMQ resumes trading automatically.</summary>
    [JsonPropertyName("paused")] public bool? Paused { get; set; }

    /// <summary>Human-readable reason Quantlab paused this edge (e.g.
    /// "1y realized Sharpe -0.93 vs backtest +0.52, regime decay 2024-onwards").
    /// Surfaced in TMQ logs + Telegram for operator visibility.</summary>
    [JsonPropertyName("paused_reason")] public string? PausedReason { get; set; }

    // ── [2026-05-17 Quantlab Kelly restoration handoff] ──
    // Quantlab now emits per-strategy Kelly haircut (fractional sizing multiplier in
    // 0..1). Default historically 0.5 (half-Kelly); 2026-05-17 op_breakout_4h_short
    // and eth_breakout_1h_long were restored to 0.65 and 0.75 respectively based on
    // 8.7y replay (CAGR +10pp, equity 1.59×). TMQ multiplies target_weights by this
    // value at consumption time so Quantlab can tune Kelly without re-deploying TMQ.
    // Missing/null → fall back to host-side default (back-compat with pre-2026-05-17
    // emitters; do NOT silently size full-Kelly).

    /// <summary>Per-strategy fractional Kelly sizing multiplier (0..1). Quantlab
    /// emits this with every cycle; TMQ multiplies each <c>target_weight</c> by it
    /// before computing notional. Null = field absent → caller falls back to
    /// <c>EdgeLab:DefaultKellyHaircut</c> (default 0.5).</summary>
    [JsonPropertyName("kelly_haircut")] public double? KellyHaircut { get; set; }

    // ── [2026-05-21 Quantlab universe-expansion handoff §3] ──
    // Quantlab proved alt-shorts are NOT independent edges but ONE correlated factor
    // ("alts fall together"). A naive stack of op_breakout_4h_short + fil_breakout_4h_short
    // + the xs-neutral SHORT leg backtested to -82% basket DD. Quantlab tags every such
    // signal with exposure_group="alt_short"; TMQ caps their COMBINED gross-short notional
    // (see EdgeLabAltShortCapEvaluator) so total alt-short beta ≈ one single edge's worth.

    /// <summary>Risk-correlation bucket for portfolio-level exposure capping
    /// (e.g. <c>"alt_short"</c>). Null/absent = ungrouped (no aggregate cap applies).
    /// Used by <see cref="EdgeLabAltShortCapEvaluator"/> to bound the combined
    /// short notional of all co-correlated alt-short legs.</summary>
    [JsonPropertyName("exposure_group")] public string? ExposureGroup { get; set; }
}
