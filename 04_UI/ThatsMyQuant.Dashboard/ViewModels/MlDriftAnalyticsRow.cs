using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MlDriftAnalyticsRow : ObservableObject
{
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _strategyTag = "";
    [ObservableProperty] private string _regimeTag = "";
    [ObservableProperty] private string _sessionTag = "";
    [ObservableProperty] private string _executionMode = "";
    [ObservableProperty] private string _promotionStage = "";
    [ObservableProperty] private int _trades;
    [ObservableProperty] private int _criticalTrades;
    [ObservableProperty] private int _elevatedTrades;
    [ObservableProperty] private decimal _maxZScore;
    [ObservableProperty] private string _impact = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private Brush _impactColor = Brushes.Gray;
}
