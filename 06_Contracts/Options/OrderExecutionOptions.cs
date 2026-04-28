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

    public void Normalize()
    {
        if (LimitFillPollIntervalMs < 250) LimitFillPollIntervalMs = 250;
        if (LimitOrderTimeoutSeconds < 0) LimitOrderTimeoutSeconds = 0;
        if (MakerUrgency < 0) MakerUrgency = 0;
        if (MakerUrgency > 1) MakerUrgency = 1;
        if (MakerPatientTimeoutMs <= 0) MakerPatientTimeoutMs = 5_000;
        if (MakerUrgentTimeoutMs <= 0) MakerUrgentTimeoutMs = 1_000;
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
