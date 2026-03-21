using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public sealed class StrategyAllocationRow
{
    public string StrategyTag { get; set; } = "";
    public string RegimeTag { get; set; } = "";
    public decimal RecommendedCapital { get; set; }
    public double AllocationPercent { get; set; }
    public string Status { get; set; } = "Watch";
    public Brush StatusColor { get; set; } = Brushes.Gray;
}
