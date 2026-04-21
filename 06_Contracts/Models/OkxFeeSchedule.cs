namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// OKX published fee schedule for VIP 0 / regular users (2024-2026). Fees are stored in bps
/// (1 bp = 0.01% = 0.0001). Use <see cref="Resolve"/> to look up maker/taker fees by instrument
/// type. Real-time fees for a given account can be queried via the authenticated
/// /api/v5/account/trade-fee endpoint — that's wired into the live path; backtest defaults to
/// this table so historical sims cost the same as the fee an unranked user actually pays today.
/// Source: OKX Fee Schedule — https://www.okx.com/fees
/// </summary>
public static class OkxFeeSchedule
{
    public enum InstrumentType
    {
        /// <summary>Spot — e.g. BTC-USDT. Higher fees than perps.</summary>
        Spot,
        /// <summary>USDT-margined perpetual swap — e.g. BTC-USDT-SWAP. Lowest fees, our target.</summary>
        PerpetualSwap,
        /// <summary>USD-margined coin-margined swap — e.g. BTC-USD-SWAP. Parity with USDT perp.</summary>
        CoinMarginedSwap,
    }

    public readonly record struct FeeTier(decimal MakerBps, decimal TakerBps);

    // VIP 0 / Regular user fees — the fee a cold-start account pays. Higher tiers get rebates.
    // These exactly match the numbers on https://www.okx.com/fees as of 2026-04.
    public static readonly FeeTier SpotVip0              = new(MakerBps:  8m, TakerBps: 10m);
    public static readonly FeeTier PerpSwapVip0          = new(MakerBps:  2m, TakerBps:  5m);
    public static readonly FeeTier CoinMarginedSwapVip0  = new(MakerBps:  2m, TakerBps:  5m);

    public static FeeTier Resolve(InstrumentType type) => type switch
    {
        InstrumentType.Spot              => SpotVip0,
        InstrumentType.PerpetualSwap     => PerpSwapVip0,
        InstrumentType.CoinMarginedSwap  => CoinMarginedSwapVip0,
        _ => SpotVip0
    };

    /// <summary>
    /// Classify an OKX instId by suffix: "BTC-USDT-SWAP" → PerpetualSwap, "BTC-USD-SWAP" →
    /// CoinMarginedSwap, anything else treated as Spot. Matches OkxExecutionService.NormalizeToOkx.
    /// </summary>
    public static InstrumentType ClassifyInstrument(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return InstrumentType.Spot;
        var s = symbol.Trim().ToUpperInvariant();
        if (!s.EndsWith("-SWAP")) return InstrumentType.Spot;
        // "-USD-SWAP" is coin-margined (inverse); "-USDT-SWAP" / "-USDC-SWAP" are linear perps.
        if (s.EndsWith("-USD-SWAP")) return InstrumentType.CoinMarginedSwap;
        return InstrumentType.PerpetualSwap;
    }
}
