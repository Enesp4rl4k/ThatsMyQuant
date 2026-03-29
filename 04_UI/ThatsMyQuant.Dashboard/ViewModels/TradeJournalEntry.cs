using System;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public class TradeJournalEntry
{
    public DateTime Time { get; set; }
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal Price { get; set; }
    public int Confidence { get; set; }
    public string Status { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string ExecutionMode { get; set; } = "";
    public string MlModelVersion { get; set; } = "";
    public string PromotionStage { get; set; } = "";
    public string StageExplanation { get; set; } = "";
    public string ExactBucketImpact { get; set; } = "";
    public string ExactBucketSummary { get; set; } = "";
    public string EdgeSummary { get; set; } = "";
    public string ExitSummary { get; set; } = "";
    public string MlDriftImpact { get; set; } = "";
    public string MlDriftSummary { get; set; } = "";
    public string VenueImpact { get; set; } = "";
    public string AllocatorImpact { get; set; } = "";
    public string ConcordanceImpact { get; set; } = "";
    public string ConcordanceSummary { get; set; } = "";
    public Brush MlDriftImpactColor { get; set; } = Brushes.Gray;
    public Brush AllocatorImpactColor { get; set; } = Brushes.Gray;
    public Brush VenueImpactColor { get; set; } = Brushes.Gray;
    public Brush ExecutionModeColor { get; set; } = Brushes.Gray;
    public Brush SideColor { get; set; } = Brushes.Gray;
    public decimal PnL { get; set; }
    public ThatsMyQuant.Contracts.Models.Trade? OriginalTrade { get; set; }
}
