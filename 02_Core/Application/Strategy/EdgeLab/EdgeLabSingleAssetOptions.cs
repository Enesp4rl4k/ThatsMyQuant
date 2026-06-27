namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Configuration block under <c>"EdgeLabSingleAsset"</c> in appsettings —
/// controls the single-asset (directional) signal consumer added 2026-05-08
/// for the $2K capital cycle.
///
/// Differs from <see cref="EdgeLabSignalOptions"/>:
///   - Reads multiple per-strategy subdirs under <see cref="InputDir"/> via
///     the <see cref="Strategies"/> list (one hosted-service instance per
///     strategy, registered in Program.cs).
///   - Single-asset signals carry exactly one symbol in <c>target_weights</c>
///     and are NOT beta-neutral. The hosted service skips the XS-style 200%
///     gross / k=2 long+short validation.
///   - Default <see cref="MinDeltaUsd"/> is 2.0 (vs 5-25 on XS) because
///     $2K-cycle position deltas are smaller per rebalance.
///   - Default <see cref="OrderType"/> is "Limit" because directional
///     single-asset trades are not as time-critical as XS rebalance windows.
/// </summary>
public sealed class EdgeLabSingleAssetOptions
{
    public const string SectionName = "EdgeLabSingleAsset";

    public bool Enabled { get; set; } = false;

    /// <summary>Directory containing per-strategy subfolders. Same path the XS
    /// service uses; the two services coexist by reading different subdirs.</summary>
    public string InputDir { get; set; } = string.Empty;

    /// <summary>List of strategy names to consume. Each is a subdir of
    /// <see cref="InputDir"/> with its own <c>latest.json</c>. Empty list = no
    /// signals consumed (idle). Program.cs spawns one hosted service per entry.</summary>
    public List<string> Strategies { get; set; } = new();

    /// <summary>Polling cadence per strategy (seconds). Default 60.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Stale-alert threshold (hours). Default 4 because single-asset
    /// strategies typically run on 1h cadence and 4h with no signal is overdue.</summary>
    public int StaleAlertHours { get; set; } = 4;

    /// <summary>USD delta floor below which rebalance is skipped. Default $2 for
    /// $2K-cycle granularity.</summary>
    public decimal MinDeltaUsd { get; set; } = 2.0m;

    /// <summary>Default order type. "Limit" preferred for directional trades to
    /// avoid taker fees; "Market" for time-critical fallback. Override per-strategy
    /// via metadata if needed (future work).</summary>
    public string OrderType { get; set; } = "Limit";

    /// <summary>Execution mode mirrors EdgeLabSignalOptions.HybridMode semantics:
    /// off / dry-run / live. Default dry-run.</summary>
    public string HybridMode { get; set; } = "dry-run";

    /// <summary>
    /// [Sprint 2 — 2026-05-10] Per-strategy override block keyed by strategy name
    /// (must match a name in <see cref="Strategies"/>). When a strategy has no
    /// override, an effective default is computed at construction time:
    /// equal-weight (1 / N) and parent-inherited hybrid-mode. Lookup is
    /// case-insensitive.
    ///
    /// JSON shape under <c>EdgeLabSingleAsset:StrategyOverrides</c>:
    /// <code>
    ///   "fee_panic_btc_long":      { "Weight": 0.50, "PromotionStatus": "verified" },
    ///   "stablecoin_30d_btc_long": { "Weight": 0.30, "PromotionStatus": "soft-promote" },
    ///   "rv_breakout_btc_long":    { "Weight": 0.20, "PromotionStatus": "soft-promote",
    ///                                 "MinPaperCyclesForLive": 10, "HybridMode": "dry-run" }
    /// </code>
    /// </summary>
    public Dictionary<string, EdgeLabStrategyOverride> StrategyOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// [Sprint 7 — 2026-05-11] Per-symbol notional cap as a fraction of basis.
    /// Caps the TOTAL EdgeLab single-asset notional exposure on a single symbol
    /// across all strategies. Defends against the multi-strategy concentration
    /// risk that emerges when several strategies target the same underlying
    /// (e.g. 4+ BTC strategies — fee_panic, stablecoin_30d, rv_breakout,
    /// btc_breakout_4h_long — cumulative weight 0.31 at $2K basis × 1.5lev =
    /// $930 single-asset exposure if all co-fire).
    ///
    /// Semantics:
    ///   - Keys: symbol identifier (case-insensitive, e.g. "BTCUSDT").
    ///   - Values: fraction of <c>PaperPortfolio.InitialBalance</c> (0..1).
    ///     Example: 0.30 on $2K basis → $600 cap.
    ///   - Missing key → no cap (back-compat).
    ///   - Cap is enforced in Live mode only. DryRun cycles log a warning when
    ///     the cap would be breached but proceed with the rebalance — synthetic
    ///     positions don't risk real capital, and the warning is useful operator
    ///     telemetry ahead of live promotion.
    ///   - Policy: SKIP-only (the cycle returns without submitting the order).
    ///     Scale-down was considered but adds promotion-gate / kill-switch
    ///     interpretation complexity; skip is cleaner.
    /// </summary>
    public Dictionary<string, decimal> PerSymbolCap { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// [2026-05-21 handoff §3] Aggregate alt-short exposure cap as a fraction of basis.
    /// Bounds the COMBINED gross-short notional of every strategy whose signal carries
    /// <c>metadata.exposure_group == "alt_short"</c> (op/fil breakout shorts + the
    /// xs-neutral basket's short leg). Quantlab proved these are one correlated factor
    /// (-82% basket DD naive); this caps total alt-short beta to ≈ one single edge's
    /// worth. Null = no cap (back-compat). Enforced in Live mode only; DryRun warns.
    /// "1 unit" ≈ one full single-asset short notional (e.g. op weight 0.11 × lev 1.5
    /// ≈ 0.165 of basis), so a default around 0.18 leaves minimal headroom.
    /// </summary>
    public decimal? AltShortCap { get; set; }

    /// <summary>
    /// Symbols counted toward the <see cref="AltShortCap"/> aggregate (case-insensitive).
    /// In Live mode the cap sums |short notional| over these symbols from the exchange.
    /// Static list covers the fixed single-asset shorts (OPUSDT, FILUSDT); the xs-neutral
    /// basket, whose short legs rotate daily, enforces the cap on its own leg via the same
    /// evaluator. Empty → no symbols aggregated → cap is a no-op.
    /// </summary>
    public List<string> AltShortSymbols { get; set; } = new();

    /// <summary>
    /// Resolves the effective override for a strategy by name. Returns the
    /// configured override if present, otherwise a synthesized default with
    /// equal-share weight.
    /// </summary>
    public EdgeLabStrategyOverride GetEffectiveOverride(string strategyName)
    {
        if (StrategyOverrides.TryGetValue(strategyName, out var ovr))
            return ovr;

        var n = Strategies?.Count ?? 1;
        return new EdgeLabStrategyOverride
        {
            Weight = n > 0 ? 1m / n : 1m,
            HybridMode = null,
            PromotionStatus = "verified",
            MinPaperCyclesForLive = 0,
            StaleAlertHours = null
        };
    }
}
