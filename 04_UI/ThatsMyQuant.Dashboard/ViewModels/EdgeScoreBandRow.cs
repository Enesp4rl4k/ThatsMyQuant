using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class EdgeScoreBandRow : ObservableObject
{
    [ObservableProperty] private string _band = "";
    [ObservableProperty] private string _executionMode = "";
    [ObservableProperty] private string _promotionStage = "";
    [ObservableProperty] private int _trades;
    [ObservableProperty] private double _winRate;
    [ObservableProperty] private double _profitFactor;
    [ObservableProperty] private decimal _totalPnL;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private Brush _pnlColor = Brushes.Gray;
}
