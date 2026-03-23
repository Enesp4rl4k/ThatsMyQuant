using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public sealed class StrategyAllocationRow
{
    public string StrategyTag { get; set; } = "";
    public string RegimeTag { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string SessionTag { get; set; } = "";
    public string PromotionStage { get; set; } = "";
    public decimal AverageVenueExecutionBias { get; set; } = 1.00m;
    public string VenuePromotionImpact { get; set; } = "";
    public string VenueExecutionSummary { get; set; } = "";
    public string ExactBucketPromotionImpact { get; set; } = "";
    public string ExactBucketSummary { get; set; } = "";
    public string ConcordanceImpact { get; set; } = "";
    public string ConcordanceSummary { get; set; } = "";
    public decimal RecommendedCapital { get; set; }
    public double AllocationPercent { get; set; }
    public string Status { get; set; } = "Watch";
    public Brush StatusColor { get; set; } = Brushes.Gray;
    public Brush ExactBucketPromotionImpactColor { get; set; } = Brushes.Gray;
    public Brush VenuePromotionImpactColor { get; set; } = Brushes.Gray;
    public Brush ConcordanceImpactColor { get; set; } = Brushes.Gray;
}
