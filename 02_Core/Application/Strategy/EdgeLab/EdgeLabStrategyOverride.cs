namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Per-strategy override block under
/// <c>EdgeLabSingleAsset:StrategyOverrides:&lt;strategy_name&gt;</c> in appsettings.
/// Lets the operator configure each EdgeLab single-asset strategy independently
/// (basis weight, hybrid mode, promotion gate) without forcing a global flag.
///
/// Backward-compat: when no override exists for a strategy listed in
/// <see cref="EdgeLabSingleAssetOptions.Strategies"/>, the parent options'
/// <c>HybridMode</c> + <c>StaleAlertHours</c> are used and the strategy gets
/// an equal share of basis (1 / N). This preserves the pre-Sprint-2 behaviour
/// where all strategies shared one HybridMode and split basis implicitly.
///
/// PhD-quant rationale (Sprint 2, 2026-05-10):
///   1. <see cref="Weight"/> turns the implicit "all strategies share $2K" model
///      into an explicit per-strategy capital allocation. Without this, a single
///      large signal can crowd out a smaller-conviction one. With this, attribution
///      is clean and the operator can hand-tune capital allocation while the HRP
///      allocator (Master Plan v3 Sprint 3.1, future) is still observation-only.
///
///   2. <see cref="PromotionStatus"/> + <see cref="MinPaperCyclesForLive"/>
///      codify the soft-promote → live transition. A strategy that came out of
///      Quantlab with a "soft-promote" verdict (passes some but not all paranoid
///      gates — e.g. n &lt; 30 or perm_p &gt; 0.05) goes live ONLY after the
///      operator-set minimum number of paper cycles complete cleanly. Default
///      0 = no gate (preserves existing operator-flipped-live behaviour for
///      already-deployed strategies). Set to a non-zero value when adding a new
///      soft-promote edge to enforce paper observation before risking capital.
///
///   3. <see cref="StaleAlertHours"/> per-strategy override closes deferred
///      bug B2 (single global StaleAlertHours doesn't accommodate strategies
///      with different rebalance cadences — daily edges should alert ~26h,
///      hourly edges ~4h).
/// </summary>
public sealed class EdgeLabStrategyOverride
{
    /// <summary>
    /// Fraction of <c>PaperPortfolio.InitialBalance</c> allocated to this
    /// strategy. Must be in [0, 1]; the hosted service multiplies the signal's
    /// own <c>target_weights</c> entry by this value.
    /// Default null = "no override" = equal share (1 / N).
    /// </summary>
    public decimal? Weight { get; set; }

    /// <summary>
    /// HybridMode override (off | dry-run | live). Default null = inherit from
    /// parent <see cref="EdgeLabSingleAssetOptions.HybridMode"/>.
    /// </summary>
    public string? HybridMode { get; set; }

    /// <summary>
    /// Promotion verdict from Quantlab. Recognised values:
    ///   verified      — all paranoid gates pass; live allowed unconditionally.
    ///   soft-promote  — partial gates pass (e.g. n &lt; 30); live allowed only
    ///                   after <see cref="MinPaperCyclesForLive"/> paper cycles.
    ///   observation   — never go live regardless of HybridMode setting; force
    ///                   dry-run. Used when the strategy is on a watchlist but
    ///                   the operator wants the consumer plumbing exercised.
    /// Default "verified" = no demotion (back-compat with strategies that pre-date
    /// this field).
    /// </summary>
    public string PromotionStatus { get; set; } = "verified";

    /// <summary>
    /// Minimum number of paper (dry-run) reconciliation cycles required before
    /// a <see cref="PromotionStatus"/>=<c>soft-promote</c> strategy is allowed
    /// to honour an operator-declared <c>HybridMode=live</c>. The reconciliation
    /// ledger row count is the source of truth (one row per cycle in dry-run).
    /// 0 = gate disabled. Default 0 for back-compat.
    /// Suggested values: 10 cycles for daily-rebalance strategies (~10 days),
    /// 30 cycles for hourly (~30 hours).
    /// </summary>
    public int MinPaperCyclesForLive { get; set; } = 0;

    /// <summary>
    /// Per-strategy stale-signal alert threshold (hours). Default null =
    /// inherit from parent <see cref="EdgeLabSingleAssetOptions.StaleAlertHours"/>.
    /// </summary>
    public int? StaleAlertHours { get; set; }
}
