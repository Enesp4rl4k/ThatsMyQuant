namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Paper-mode portfolio-level exposure caps (section: Paper:PortfolioLimits).
///
/// These caps are the blunt instrument that prevents a multi-symbol
/// Trend+Range+Breakout firing day from stacking 3x intended exposure in
/// correlated assets — Kelly sizing per trade ignores portfolio-wide correlation
/// drag, so portfolio-level limits stand as the last-line guard.
/// </summary>
public sealed class PaperPortfolioLimitsOptions
{
    /// <summary>
    /// Maximum number of simultaneous open positions. Default 2 preserves
    /// historical behaviour; bump to 6 for the diversified multi-edge portfolio.
    /// </summary>
    public int MaxConcurrentPositions { get; set; } = 2;

    /// <summary>
    /// Maximum total open notional as a fraction of current equity.
    /// 0.40 = 40% of equity gross-exposed at any moment.
    /// </summary>
    public decimal MaxTotalNotionalRatio { get; set; } = 0.40m;

    /// <summary>
    /// Number of recent closes inspected for the consecutive-loss cooldown.
    /// </summary>
    public int ConsecutiveLossLookback { get; set; } = 4;

    /// <summary>
    /// Consecutive losses required to trigger cooldown (no new entries).
    /// </summary>
    public int ConsecutiveLossCooldownThreshold { get; set; } = 3;

    /// <summary>
    /// Daily drawdown (as fraction of initial balance) that halts new entries
    /// for the rest of the session. Default 0.02 = 2%.
    /// </summary>
    public decimal DailyLossLimitRatio { get; set; } = 0.02m;
}
