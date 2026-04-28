namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Risk-adjusted optimization weights and gating thresholds by risk profile.
/// Section: Backtest:ProfitMax
/// </summary>
public sealed class ProfitMaximizationOptions
{
    public ProfitProfileOptions Low { get; set; } = new()
    {
        PnlWeight = 0.30,
        SharpeWeight = 0.40,
        RrWeight = 0.20,
        DrawdownPenaltyWeight = 0.10,
        MinSharpe = 0.80,
        MinAverageRr = 1.00,
        MinWinRatePercent = 52.0,
        MinExpectancy = 0m,
        MaxDailyDrawdownPercent = 2.0,
        // Safe mode: no leverage. Capital preservation first.
        Leverage = 1.0m,
        MaxDrawdownBudget = 0.02m,      // 2% DD budget → throttle halves leverage at 1.2%
        DrawdownThrottleFraction = 0.60m
    };

    public ProfitProfileOptions Moderate { get; set; } = new()
    {
        PnlWeight = 0.40,
        SharpeWeight = 0.30,
        RrWeight = 0.20,
        DrawdownPenaltyWeight = 0.10,
        MinSharpe = 0.70,
        MinAverageRr = 0.90,
        MinWinRatePercent = 50.0,
        MinExpectancy = -2m,
        MaxDailyDrawdownPercent = 2.5,
        // Normal mode: moderate leverage for pro-grade risk/return balance.
        Leverage = 3.0m,
        MaxDrawdownBudget = 0.035m,     // 3.5% DD budget
        DrawdownThrottleFraction = 0.60m
    };

    public ProfitProfileOptions Aggressive { get; set; } = new()
    {
        PnlWeight = 0.50,
        SharpeWeight = 0.20,
        RrWeight = 0.20,
        DrawdownPenaltyWeight = 0.10,
        MinSharpe = 0.40,
        MinAverageRr = 0.85,
        MinWinRatePercent = 48.0,
        MinExpectancy = -8m,
        MaxDailyDrawdownPercent = 3.0,
        // Aggressive mode: higher leverage for higher return. Still safety-capped at 15x
        // and DD-throttled at 60% of 6% budget.
        Leverage = 7.0m,
        MaxDrawdownBudget = 0.06m,      // 6% DD budget
        DrawdownThrottleFraction = 0.60m
    };

    public ProfitProfileOptions Resolve(string? riskProfile)
    {
        var key = riskProfile?.Trim().ToLowerInvariant();
        if (key is null)
        {
            return Moderate;
        }

        if (key.Contains("low"))
        {
            return Low;
        }

        if (key.Contains("agg"))
        {
            return Aggressive;
        }

        return Moderate;
    }
}

public sealed class ProfitProfileOptions
{
    public double PnlWeight { get; set; } = 0.4;
    public double SharpeWeight { get; set; } = 0.3;
    public double RrWeight { get; set; } = 0.2;
    public double DrawdownPenaltyWeight { get; set; } = 0.1;

    public double MinSharpe { get; set; } = 0.7;
    public double MinAverageRr { get; set; } = 0.9;
    public double MinWinRatePercent { get; set; } = 50.0;
    public decimal MinExpectancy { get; set; } = -2m;
    public double MaxDailyDrawdownPercent { get; set; } = 2.5;

    // Leverage + risk budget (2026-04-24). Tied to risk-profile so Safe/Normal/Aggressive
    // semantics carry through to position sizing. Hard-capped in the runners at 15x.
    public decimal Leverage { get; set; } = 1.0m;
    public decimal MaxDrawdownBudget { get; set; } = 0.03m;
    public decimal DrawdownThrottleFraction { get; set; } = 0.60m;
}
