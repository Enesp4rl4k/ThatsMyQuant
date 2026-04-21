namespace ThatsMyQuant.Application.Strategy;

public sealed class BacktestCostModel
{
    // OKX taker fees: 8 bps spot / 5 bps USDT-margined perp. Default to spot-taker as the
    // realistic worst case; callers can override for perp-specific sessions.
    public decimal CommissionBps { get; init; } = 8m;

    // Half-spread always paid on market entries/exits. OKX 1h BTC-USDT typical bid-ask ~3 bps.
    // Applied in addition to SlippageBps (which models adverse-fill beyond mid).
    public decimal SpreadBps { get; init; } = 3m;

    // Multiplier applied to SpreadBps in Volatile regime (breakouts, news spikes). OKX books widen
    // 3-5x during vol events. Backtest must price this or live breakouts will miss realized R:R.
    public decimal VolatileSpreadMultiplier { get; init; } = 3m;

    // Baseline adverse-fill (book-crossing) component. ATR-proportional slippage is layered on top.
    public decimal SlippageBps { get; init; } = 2m;

    // Multiplier applied to (TrueRange / Price). Setting 0 falls back to flat slippage only.
    // 0.05 means a bar with 2% range adds 10 bps of slippage to the fill.
    public decimal AtrSlippageMultiplier { get; init; } = 0.05m;

    // Cap total slippage at this fraction of the entry bar range (high-low). Prevents unrealistic
    // mid→SL slippage on 20% candles when ATR multiplier compounds with wide spreads.
    public decimal MaxSlippageFractionOfBarRange { get; init; } = 0.50m;

    // Size-impact penalty: extra bps = SizeImpactBpsPerNotionalPct * (orderNotional / barVolume) * 100.
    // 0 disables (legacy). 5 means an order equal to 100% of bar volume adds 500 bps of slip.
    public decimal SizeImpactBpsPerNotionalPct { get; init; } = 5m;

    public decimal FundingBpsPer8h { get; init; } = 1m;

    // Multiplier applied to funding in Volatile regime. OKX BTC funding swings 5-50 bps
    // annualized during stress; set >1 to stress-test funding-side PnL.
    public decimal FundingVolatilityMultiplier { get; init; } = 1m;

    public decimal CommissionRate => CommissionBps / 10_000m;
    public decimal SlippageRate => SlippageBps / 10_000m;
    public decimal SpreadRate => SpreadBps / 10_000m;

    // ----- Maker-entry settings -----
    // Maker commission on OKX perp: 2 bps (vs 5 bps taker). Spot: 8 bps maker / 10 bps taker.
    // Defaults to perp economics as the Phase-1 target.
    public decimal MakerCommissionBps { get; init; } = 2m;

    // Opt-in: true routes entries through the maker limit path (signal -> post limit N bps
    // away from close -> look-ahead up to MakerEntryTimeoutBars bars for a touch -> fill at
    // limit with maker commission, no spread, no slippage). If the limit never touches, the
    // entry is skipped entirely (no force-conversion to taker — preserves cost discipline).
    public bool UseMakerEntries { get; init; } = true;

    // How far inside the candle close to post the limit. Do NOT raise above ~3 bps without
    // re-evaluating per-timeframe: 2026-04-18 experiment at 8 bps on BTC 4h 365d was a no-op
    // on fill rate (MakerNoFill stayed at 1) because BTC 4h bar ranges are 80–200 bps, so any
    // sub-20 bp offset is trivially traversed within the 2-bar timeout. The small price-level
    // shifts that did change blew PBO up 0.365 → 0.704 via grid-search amplification (reliability
    // gate fail). The lever is inert at BTC 4h; re-test on 15m/5m where bar ranges are closer
    // to the offset size.
    public decimal MakerEntryOffsetBps { get; init; } = 3m;

    // Bars to wait for a limit fill before abandoning the entry. 2 bars = 2 * timeframe.
    // Short enough that a stale signal doesn't execute, long enough to catch realistic dips.
    public int MakerEntryTimeoutBars { get; init; } = 2;

    public decimal MakerCommissionRate => MakerCommissionBps / 10_000m;
    public decimal MakerEntryOffsetRate => MakerEntryOffsetBps / 10_000m;
}
