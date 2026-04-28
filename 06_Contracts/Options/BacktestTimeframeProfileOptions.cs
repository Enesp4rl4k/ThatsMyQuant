namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Per-timeframe strategy behavior profile for backtests.
/// Section: Backtest:Profiles
/// </summary>
public sealed class BacktestTimeframeProfileOptions
{
    // Tuning: scalp TFs need higher MinRR to clear costs (~20bps round-trip);
    // higher TFs can run more size but need same RR floor for realized R:R discipline.

    public BacktestProfile OneMinute { get; set; } = new()
    {
        MinConfidence = 68,
        MinRiskReward = 1.80m,
        TrendPositionCapPercent = 4.0m,
        RangePositionCapPercent = 2.5m,
        VolatilePositionCapPercent = 1.2m,
        AtrSlippageScalar = 1.40m
    };

    public BacktestProfile FiveMinute { get; set; } = new()
    {
        MinConfidence = 65,
        MinRiskReward = 1.70m,
        TrendPositionCapPercent = 5.0m,
        RangePositionCapPercent = 3.0m,
        VolatilePositionCapPercent = 1.5m,
        AtrSlippageScalar = 1.25m
    };

    public BacktestProfile FifteenMinute { get; set; } = new()
    {
        MinConfidence = 63,
        MinRiskReward = 1.60m,
        TrendPositionCapPercent = 6.0m,
        RangePositionCapPercent = 3.5m,
        VolatilePositionCapPercent = 1.6m,
        AtrSlippageScalar = 1.10m
    };

    public BacktestProfile OneHour { get; set; } = new()
    {
        MinConfidence = 55,
        MinRiskReward = 1.50m,
        TrendPositionCapPercent = 12.0m,
        RangePositionCapPercent = 6.0m,
        VolatilePositionCapPercent = 2.5m,
        AtrSlippageScalar = 1.00m
    };

    public BacktestProfile FourHour { get; set; } = new()
    {
        MinConfidence = 60,
        MinRiskReward = 1.50m,
        // Do NOT raise these naively — 2026-04-18 experiment at 1.7× caps (24/12/5) regressed
        // Sharpe 0.59→0.29, PnL $256→$203, pooled OOS Sharpe +1.30→+0.84, PBO 0.365→0.435, with
        // identical trade count/WR. Reading: caps were clamping downside on high-confidence
        // losers more than they were clamping upside on winners. Confidence is asymmetrically
        // noisy — larger size on "high-conf" signals hurts because those signals overfit. Sizing
        // leverage is not free on this strategy. If sizing becomes the lever again, the real
        // fix is recalibrating confidence → size mapping (shrink high-conf less aggressively),
        // not expanding the cap ceiling.
        TrendPositionCapPercent = 14.0m,
        RangePositionCapPercent = 7.0m,
        VolatilePositionCapPercent = 3.0m,
        AtrSlippageScalar = 0.90m
    };

    public BacktestProfile OneDay { get; set; } = new()
    {
        MinConfidence = 58,
        MinRiskReward = 1.50m,
        TrendPositionCapPercent = 16.0m,
        RangePositionCapPercent = 8.0m,
        VolatilePositionCapPercent = 4.0m,
        AtrSlippageScalar = 0.80m
    };
}

public sealed class BacktestProfile
{
    public int MinConfidence { get; set; } = 50;
    public decimal MinRiskReward { get; set; } = 1.10m;
    public decimal TrendPositionCapPercent { get; set; } = 12.0m;
    public decimal RangePositionCapPercent { get; set; } = 7.0m;
    public decimal VolatilePositionCapPercent { get; set; } = 3.0m;
    public decimal AtrSlippageScalar { get; set; } = 1.0m;

    /// <summary>TP distance as a multiple of ATR. Default 2.5× ensures 2.5:1 gross RR.</summary>
    public decimal TakeProfitAtrMultiple { get; set; } = 2.5m;

    /// <summary>SL distance as a multiple of ATR. Default 1.0× ATR — tight but respects vol.</summary>
    public decimal StopLossAtrMultiple { get; set; } = 1.0m;

    /// <summary>Whether to use ATR-scaled TP/SL instead of indicator-derived levels. Default: true.</summary>
    public bool UseAtrScaledExits { get; set; } = true;
}
