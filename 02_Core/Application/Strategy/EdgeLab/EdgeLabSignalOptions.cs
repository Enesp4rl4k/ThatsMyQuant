namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Configuration block under <c>"EdgeLab"</c> in appsettings — controls the
/// signals/&lt;strategy&gt;/latest.json consumer. Defaults match the
/// realized_vol_4h_xs_reverse approved edge (signals/README.md, 2026-05-01).
/// </summary>
public sealed class EdgeLabSignalOptions
{
    public const string SectionName = "EdgeLab";

    public bool Enabled { get; set; } = true;

    /// <summary>Directory containing per-strategy subfolders. Each holds
    /// <c>latest.json</c> + history. Cross-repo path; e.g.
    /// <c>C:/Users/parlak/Downloads/Quantlab/signals</c>.</summary>
    public string InputDir { get; set; } = string.Empty;

    /// <summary>Strategy folder name; combined with <see cref="InputDir"/>
    /// to locate latest.json. Used both for path and contract check
    /// (<c>doc.strategy == Strategy</c>).</summary>
    public string Strategy { get; set; } = "realized_vol_4h_xs_reverse";

    /// <summary>Expected timeframe; cross-checked against
    /// <c>metadata.timeframe</c>. Drives the staleness window
    /// (max age = 2× this) and the t+1 bar timing gate.</summary>
    public string Timeframe { get; set; } = "4h";

    /// <summary>Polling cadence. Short (60s) so we catch a new file fast and
    /// can fire near the t+1 bar open even though signals only appear every
    /// timeframe. Idempotent — re-reads dedup via <c>as_of_bar</c>.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Skip dust orders. Computed against the absolute USD delta at
    /// the current mark — anything smaller is no-oped to avoid burning fees
    /// on rebalance noise inside a fixed allocation cycle.</summary>
    public decimal MinDeltaUsd { get; set; } = 25.0m;

    /// <summary>Order type for the fills. Contract pt 7 says fill near
    /// <c>as_of_bar + 1 timeframe</c> open — Market is the deterministic
    /// way to hit that boundary on the OKX demo book.</summary>
    public string OrderType { get; set; } = "Market";

    /// <summary>
    /// Execution mode, mirrors Funding/Pairs convention:
    ///   <c>off</c>     — skip the apply step entirely, no log spam, no orders.
    ///   <c>dry-run</c> — run full validate / dedup / timing / delta pipeline but
    ///                    log "WOULD submit ..." instead of calling
    ///                    <see cref="ThatsMyQuant.Contracts.Interfaces.IExchangeService.ExecuteOrderAsync"/>.
    ///                    Default — pre-registered Funding-Long / Pairs-Long live tests
    ///                    keep running uncontaminated while we validate the EdgeLab
    ///                    consumer plumbing on real signal cadence.
    ///   <c>live</c>    — submit real OKX orders. Only flip after dry-run has
    ///                    cleanly observed N≥4 cycles and a per-strategy ledger
    ///                    is wired up so EdgeLab doesn't fight other strategies
    ///                    on the shared net position.
    /// </summary>
    public string HybridMode { get; set; } = "dry-run";

    /// <summary>
    /// 2026-05-07: long-stale warning threshold (hours). When no fresh signal has
    /// been read for this many hours, a Telegram alert fires (with cooldown of the
    /// same duration so it's at-most one alert per period).
    /// Catches:
    ///   - Quantlab cron broken
    ///   - STRATEGY_REGISTRY empty post-retraction (5/3 scenario)
    ///   - Schema/timeframe drift sustained
    /// Default 24h; tighten to 12 if you want faster surface.
    /// </summary>
    public int StaleAlertHours { get; set; } = 24;

    /// <summary>
    /// 2026-05-17: Fallback fractional Kelly multiplier when a signal artifact does
    /// NOT include <c>metadata.kelly_haircut</c>. Quantlab handoff 2026-05-17:
    ///   - Default 0.5 = half-Kelly (legacy behaviour pre-restoration).
    ///   - Quantlab's new emitter writes per-strategy values
    ///     (op_breakout_4h_short=0.65, eth_breakout_1h_long=0.75 as of 2026-05-17);
    ///     those override this default when present.
    /// Leaving this as 0.5 means a hypothetical regression on the Quantlab side
    /// degrades GRACEFULLY to half-Kelly rather than silently sizing full-Kelly.
    /// </summary>
    public double DefaultKellyHaircut { get; set; } = 0.5;

    /// <summary>
    /// 2026-06-27: Expected gross exposure Σ|w| the producer targets for this strategy.
    /// The reader checks Σ|w| matches this (within 1e-2) so a dropped/duplicated/zeroed leg
    /// is still caught; the dollar-neutral Σw≈0 safety check stays strict regardless.
    ///   - Classic XS edges (realized_vol_4h_xs_reverse): 200% gross, k long + k short at
    ///     1/k each → 2.0 (the default; every legacy block validates unchanged).
    ///   - $2K-cycle <c>xs_momentum_neutral_panel</c>: a deliberately smaller 50%-gross book
    ///     (k=8 names at ±0.0625) → 0.5. Quantlab 2026-06 sizing decision; emitting 0.5 gross
    ///     made the old hardcoded-2.0 check reject every cycle as WeightAsymmetry.
    /// Must be &gt; 0. Set per strategy block in appsettings to match the producer's gross.
    /// </summary>
    public double ExpectedGross { get; set; } = 2.0;
}
