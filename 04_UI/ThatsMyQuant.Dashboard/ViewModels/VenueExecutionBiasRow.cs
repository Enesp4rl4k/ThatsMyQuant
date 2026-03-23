using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class VenueExecutionBiasRow : ObservableObject
{
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _strategyTag = "";
    [ObservableProperty] private string _regimeTag = "";
    [ObservableProperty] private string _sessionTag = "";
    [ObservableProperty] private string _venueExecutionScope = "";
    [ObservableProperty] private string _executionMode = "";
    [ObservableProperty] private string _promotionStage = "";
    [ObservableProperty] private int _trades;
    [ObservableProperty] private decimal _averageBias = 1.00m;
    [ObservableProperty] private decimal _totalPnL;
    [ObservableProperty] private string _promotionImpact = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _concordanceImpact = "";
    [ObservableProperty] private string _concordanceSummary = "";
    [ObservableProperty] private Brush _biasColor = Brushes.Gray;
    [ObservableProperty] private Brush _pnlColor = Brushes.Gray;
    [ObservableProperty] private Brush _promotionImpactColor = Brushes.Gray;
    [ObservableProperty] private Brush _concordanceImpactColor = Brushes.Gray;
}
