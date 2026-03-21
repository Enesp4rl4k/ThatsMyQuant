namespace ThatsMyQuant.Dashboard.Services;

public sealed class StrategyHealthSnapshot
{
    public string StrategyTag { get; set; } = "";
    public string RegimeTag { get; set; } = "";
    public int Trades { get; set; }
    public double WinRate { get; set; }
    public double ProfitFactor { get; set; }
    public decimal TotalPnL { get; set; }
    public bool ManuallyDisabled { get; set; }
    public bool ForceEnabled { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}
