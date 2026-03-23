using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class StrategyLeaderboardRow : ObservableObject
{
    [ObservableProperty] private string _strategyTag = "";
    [ObservableProperty] private string _regimeTag = "";
    [ObservableProperty] private string _executionMode = "";
    [ObservableProperty] private string _promotionStage = "";
    [ObservableProperty] private int _trades;
    [ObservableProperty] private double _winRate;
    [ObservableProperty] private double _profitFactor;
    [ObservableProperty] private decimal _totalPnL;
    [ObservableProperty] private decimal _averageVenueExecutionBias = 1.00m;
    [ObservableProperty] private string _venuePromotionImpact = "";
    [ObservableProperty] private string _venueExecutionSummary = "";
    [ObservableProperty] private string _exactBucketPromotionImpact = "";
    [ObservableProperty] private string _exactBucketSummary = "";
    [ObservableProperty] private string _concordanceImpact = "";
    [ObservableProperty] private string _concordanceSummary = "";
    [ObservableProperty] private Brush _exactBucketPromotionImpactColor = Brushes.Gray;
    [ObservableProperty] private Brush _venuePromotionImpactColor = Brushes.Gray;
    [ObservableProperty] private Brush _concordanceImpactColor = Brushes.Gray;
    [ObservableProperty] private Brush _pnlColor = Brushes.Gray;
}
