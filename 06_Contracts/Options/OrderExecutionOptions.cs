namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Controls live order execution behaviour on supported venues (currently OKX).
/// </summary>
public sealed class OrderExecutionOptions
{
    public const string SectionName = "OrderExecution";

    /// <summary>
    /// Seconds to wait for a placed limit order to fill before cancelling it
    /// and re-submitting as a market order. 0 disables the fallback (default).
    /// </summary>
    public int LimitOrderTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Poll interval (ms) while waiting for a limit order to fill during the
    /// timeout window. Values below 250 are clamped to 250.
    /// </summary>
    public int LimitFillPollIntervalMs { get; set; } = 1000;

    /// <summary>
    /// When true, limit orders are submitted with OKX <c>ordType=post_only</c>,
    /// which rejects the order if it would cross the book (guaranteed maker fill
    /// → maker rebate). Paired with <see cref="LimitOrderTimeoutSeconds"/>, a
    /// post-only limit that does not fill within the window is cancelled and
    /// re-submitted as a taker market order.
    /// </summary>
    public bool PostOnlyMode { get; set; } = false;

    /// <summary>
    /// [P1.4] When true, opening Market signals are rewritten to maker-Limit orders
    /// using <see cref="ThatsMyQuant.Application.Strategy.MakerExecutionPolicy"/>.
    /// Requires a live order-book snapshot (book quote drives the maker offset).
    /// Combine with <see cref="PostOnlyMode"/>=true and <see cref="LimitOrderTimeoutSeconds"/>&gt;0
    /// for the full "post-only with timeout → market fallback" flow.
    /// reduceOnly / closing orders bypass routing (guaranteed-exit semantics).
    /// </summary>
    public bool EnableMakerRouting { get; set; } = false;

    /// <summary>
    /// [P1.4] Urgency dial in [0, 1] passed to <see cref="ThatsMyQuant.Application.Strategy.MakerExecutionPolicy.Plan"/>.
    /// 0 = patient (sit at top of book, longer timeout), 1 = aggressive (one tick inside, short timeout).
    /// 0.3 is a sensible default for pairs/funding mean-reversion entries — book is usually
    /// in our favor at the entry threshold so a patient maker fills cheaply.
    /// </summary>
    public double MakerUrgency { get; set; } = 0.3;

    /// <summary>
    /// [P1.4] Patient-mode timeout (urgency=0). Default 5000 ms — shorter than the policy's
    /// 30s default because crypto books move quickly enough that stale post-only orders
    /// at the inside drift away from edge before they fill.
    /// </summary>
    public int MakerPatientTimeoutMs { get; set; } = 5_000;

    /// <summary>
    /// [P1.4] Urgent-mode timeout (urgency=1). Default 1000 ms.
    /// </summary>
    public int MakerUrgentTimeoutMs { get; set; } = 1_000;

    /// <summary>
    /// [Sprint 1 — Track B, 2026-05-10] Edge in basis points to sit inside the
    /// maker side of the book when <see cref="PostOnlyMode"/> is enabled and a
    /// live order-book snapshot is available. The effective limit price becomes:
    ///   BUY  → bestBid × (1 − MakerEdgeBps/10000)
    ///   SELL → bestAsk × (1 + MakerEdgeBps/10000)
    /// This guarantees the post_only flag does not reject the order as
    /// "would_match" (sCode 51015) which is what was happening when the consumer
    /// passed mid-price as <c>signal.Price</c> — mid is inside the spread and
    /// either crosses immediately (rejected) or fails to fill within the limit
    /// timeout, falling through to the Market fallback (= 100% taker).
    ///
    /// 1.0 bp ≈ 0.01% — small enough that it does not meaningfully shift entry,
    /// large enough that natural book-jitter (sub-tick noise) does not produce
    /// would-match rejections. Set to 0 to disable the nudge (fall back to using
    /// the raw <c>signal.Price</c> caller provided).
    ///
    /// Independent of <see cref="EnableMakerRouting"/> — that flag is for the
    /// Market→Limit rewrite path in ExecuteTradeHandler. This nudge applies to
    /// any caller that already submits a Limit signal (e.g. EdgeLabSingleAsset).
    /// </summary>
    public decimal MakerEdgeBps { get; set; } = 1.0m;

    /// <summary>
    /// [Sprint 1 — Track B, 2026-05-10] When true and the maker price nudge
    /// (see <see cref="MakerEdgeBps"/>) cannot be applied because there is no
    /// live order-book snapshot for the symbol, refuse to submit the order
    /// instead of submitting it at the (mid) <c>signal.Price</c> — which would
    /// almost certainly post-only-reject or timeout-to-market.
    /// Default false to preserve existing behaviour for non-streaming-symbol
    /// flows; flip to true for strict pre-registered tests where a single taker
    /// fill contaminates the maker-fill-ratio gate.
    /// </summary>
    public bool RequireOrderBookForPostOnly { get; set; } = false;

    /// <summary>
    /// [Sprint 1 — Track B, 2026-05-10] When OKX rejects a post_only order with
    /// sCode 51015 ("would match an existing order") — typically because the
    /// book moved between snapshot read and order submission — retry up to this
    /// many times, widening <see cref="MakerEdgeBps"/> by 0.5 bp per attempt.
    /// 0 disables retries (single attempt). Default 2 → up to 3 attempts total
    /// at edges {1.0, 1.5, 2.0} bp.
    /// </summary>
    public int PostOnlyRejectRetries { get; set; } = 2;

    /// <summary>
    /// [Sprint 1 — Track B, 2026-05-10] Edge widening per retry attempt, in bp.
    /// </summary>
    public decimal PostOnlyRejectRetryWideningBps { get; set; } = 0.5m;

    /// <summary>
    /// [2026-05-13] After <c>TryCancelOrderAsync</c> returns true (HTTP 200 + code="0"),
    /// poll GET /trade/order this many times to confirm OKX-side state actually moved to
    /// "canceled" / "mmp_canceled" before submitting the market fallback.
    /// Bug 2026-05-12: cancel HTTP 200 ≠ cancel applied. On OKX demo congestion the cancel
    /// can lag or be silently dropped, so blindly submitting market produces a duplicate
    /// (both limit AND market live simultaneously → 2× position risk).
    /// 0 disables verification (revert to legacy blind behaviour). Default 3 → ~1.5s budget
    /// at default poll delay.
    /// </summary>
    public int CancelVerifyPollAttempts { get; set; } = 3;

    /// <summary>
    /// [2026-05-13] Delay (ms) between cancel-verify polls. Clamped to [100, 5000].
    /// </summary>
    public int CancelVerifyPollDelayMs { get; set; } = 500;

    /// <summary>
    /// [2026-05-13] Max attempts to submit the market fallback when OKX rejects with
    /// sCode 51008 / "Systems are busy" / "market volatility" style transient errors.
    /// Each retry uses exponential backoff (500ms, 1000ms, 2000ms ...).
    /// 1 disables retry (single attempt). Default 3.
    /// Outcome=Unknown (HTTP error, no clOrdId returned) does NOT retry — the order may
    /// have filled silently; reconcile loop resolves.
    /// </summary>
    public int MarketFallbackRetryAttempts { get; set; } = 3;

    /// <summary>
    /// [2026-05-14 Day 5 — B1 smart routing] When true, after cancel-verify confirms the
    /// post-only limit was canceled, try an IOC (Immediate-Or-Cancel) limit at an
    /// aggressive cross-side price BEFORE falling back to a pure market order. IOC gives
    /// price protection: the order fills only if the venue can match within
    /// <see cref="SmartRoutingIocPriceOffsetBps"/> of the best opposite side. Anything
    /// not filled within ~1s is canceled (no resting order). This trims slippage by
    /// 2-5bp vs naive market on volatile books, while still being a taker.
    /// Default false (opt-in) until validated against observed slippage.
    /// </summary>
    public bool SmartRoutingIocEnabled { get; set; } = false;

    /// <summary>
    /// [2026-05-14 Day 5 — B1] How far past the touching opposite-side quote to set the
    /// IOC limit price. e.g. 5bp on an ETH bid of $2300 = $1.15 above the ask for a BUY.
    /// Anything not filled at this price band is canceled and the fallback proceeds to
    /// market. 0 disables the offset (use exact opposite quote). Default 5bp.
    /// </summary>
    public decimal SmartRoutingIocPriceOffsetBps { get; set; } = 5.0m;

    public void Normalize()
    {
        if (LimitFillPollIntervalMs < 250) LimitFillPollIntervalMs = 250;
        if (LimitOrderTimeoutSeconds < 0) LimitOrderTimeoutSeconds = 0;
        if (MakerUrgency < 0) MakerUrgency = 0;
        if (MakerUrgency > 1) MakerUrgency = 1;
        if (MakerPatientTimeoutMs <= 0) MakerPatientTimeoutMs = 5_000;
        if (MakerUrgentTimeoutMs <= 0) MakerUrgentTimeoutMs = 1_000;
        if (MakerEdgeBps < 0m) MakerEdgeBps = 0m;
        if (PostOnlyRejectRetries < 0) PostOnlyRejectRetries = 0;
        if (PostOnlyRejectRetryWideningBps < 0m) PostOnlyRejectRetryWideningBps = 0m;
        if (CancelVerifyPollAttempts < 0) CancelVerifyPollAttempts = 0;
        if (CancelVerifyPollDelayMs < 100) CancelVerifyPollDelayMs = 100;
        if (CancelVerifyPollDelayMs > 5000) CancelVerifyPollDelayMs = 5000;
        if (MarketFallbackRetryAttempts < 1) MarketFallbackRetryAttempts = 1;
        if (SmartRoutingIocPriceOffsetBps < 0m) SmartRoutingIocPriceOffsetBps = 0m;
    }
}

/// <summary>
/// Live-execution slippage guard. When absolute entry slippage exceeds the
/// threshold, an operator alert is fired (Telegram / log) so the operator can
/// investigate before the edge decays. Values in basis points (1bp = 0.01%).
/// </summary>
public sealed class SlippageAlertOptions
{
    public const string SectionName = "SlippageAlert";

    public bool Enabled { get; set; } = true;

    /// <summary>Threshold in basis points. Default 15bp (~$0.15 on a $100 price).</summary>
    public decimal ThresholdBps { get; set; } = 15m;

    /// <summary>Minimum seconds between consecutive alerts for the same symbol to avoid flooding.</summary>
    public int CooldownSeconds { get; set; } = 300;
}
