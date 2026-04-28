namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Stablecoin denomination mix for portfolio capital. Defends against single-stablecoin
/// depeg risk (Tether 2022 briefly 0.97, UST May 2022 collapsed to 0.01).
///
/// Default split: 70% USDT / 30% USDC. This is NOT pairs execution — our trades still
/// execute in -USDT perpetuals — it's about the CASH reserve denomination between
/// rebalances. On circuit-breaker trip or Volatile-cash allocation, the cash sits in
/// this split rather than 100% USDT.
///
/// For paper trading we just track notional. For live, operator must maintain the split
/// on-exchange manually (via spot conversions) until auto-rebalance is implemented.
/// </summary>
public sealed class StablecoinAllocationOptions
{
    /// <summary>Fraction of cash held in USDT. Default 0.70.</summary>
    public decimal UsdtFraction { get; set; } = 0.70m;

    /// <summary>Fraction of cash held in USDC. Default 0.30.</summary>
    public decimal UsdcFraction { get; set; } = 0.30m;

    /// <summary>When a stablecoin depegs by this much, auto-flight-to-quality. Default 1.5%.</summary>
    public decimal DepegAlertThreshold { get; set; } = 0.015m;

    /// <summary>Normalise fractions to sum to 1.0.</summary>
    public void Normalise()
    {
        var total = UsdtFraction + UsdcFraction;
        if (total <= 0m) { UsdtFraction = 0.70m; UsdcFraction = 0.30m; return; }
        UsdtFraction /= total;
        UsdcFraction /= total;
    }
}
