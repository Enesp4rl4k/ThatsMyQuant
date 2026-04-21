namespace ThatsMyQuant.Application.Strategy;

public static class RiskCalculator
{
    // Fractional Kelly. Quarter-Kelly (0.25) is appropriate once live track record exceeds ~100
    // trades. Before that, one-tenth Kelly is the correct prior because win% and PF are both
    // estimated with high variance from small samples, and Kelly is convex in those estimates
    // (overestimating either blows up size). Caller picks via liveTradeCount.
    //
    // Confirmed no-op under current router profiles: MaxPositionSizePercent clamps kellyFraction
    // × any multiplier below the per-regime cap. 2026-04-18 experiment raised this to 0.50 and
    // saw identical PnL/MaxDD/trade count → cap binds first. If sizing is the lever, raise the
    // router cap in BacktestTimeframeProfileOptions, not this multiplier.
    public const double KellyFractionMultiplier = 0.25;
    public const double ColdStartKellyMultiplier = 0.10;
    public const int LiveTradesToUnlockFullKelly = 100;

    public static decimal CalculateKellyPositionSize(double winProbability, double profitFactor, decimal maxPositionSizePercent)
        => CalculateKellyPositionSize(winProbability, profitFactor, maxPositionSizePercent, pboScore: null, liveTradeCount: int.MaxValue);

    /// <summary>
    /// PBO-aware Kelly. <paramref name="pboScore"/> (0..1) shrinks win% toward 0.5; <paramref name="liveTradeCount"/>
    /// selects fractional multiplier — under the threshold, Kelly is one-tenth. This is the single lever
    /// that prevents cold-start accounts from being oversized by backtest-implied win rates.
    /// </summary>
    public static decimal CalculateKellyPositionSize(
        double winProbability,
        double profitFactor,
        decimal maxPositionSizePercent,
        double? pboScore,
        int liveTradeCount)
    {
        if (winProbability <= 0 || profitFactor <= 0) return 0m;

        double p = winProbability;
        if (pboScore.HasValue)
        {
            // PBO=0 → full trust; PBO=1 → collapse to break-even (p=0.5).
            var pbo = Math.Clamp(pboScore.Value, 0.0, 1.0);
            p = p * (1 - pbo) + 0.5 * pbo;
        }
        double q = 1.0 - p;
        double b = profitFactor;

        double kellyFraction = 0;
        if (b > 0)
        {
            kellyFraction = Math.Max(0, (b * p - q) / b);
        }

        var fracMult = liveTradeCount >= LiveTradesToUnlockFullKelly
            ? KellyFractionMultiplier
            : ColdStartKellyMultiplier;
        kellyFraction *= fracMult;

        decimal positionSize = Math.Min((decimal)kellyFraction, maxPositionSizePercent / 100m);
        return Math.Max(0m, positionSize);
    }
}
