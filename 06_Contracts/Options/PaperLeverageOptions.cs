namespace ThatsMyQuant.Contracts.Options;

using ThatsMyQuant.Contracts.Enums;

/// <summary>
/// Paper-mode leverage and short settings (section: Paper:Leverage).
/// </summary>
public sealed class PaperLeverageOptions
{
    public bool Enabled { get; set; } = true;
    public decimal DefaultLeverage { get; set; } = 1m;
    public decimal MaxLeverage { get; set; } = 3m;
    public MarginMode DefaultMarginMode { get; set; } = MarginMode.Isolated;
    public decimal MaintenanceMarginRate { get; set; } = 0.005m;
    public decimal LiquidationFeeRate { get; set; } = 0.0006m;
    public decimal FundingRateBpsPer8h { get; set; } = 1m;
}
