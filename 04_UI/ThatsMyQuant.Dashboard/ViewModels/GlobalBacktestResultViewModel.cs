using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class GlobalBacktestResultViewModel : ObservableObject
{
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private int _totalTrades;
    [ObservableProperty] private double _winRate;
    [ObservableProperty] private decimal _totalPnL;
    [ObservableProperty] private decimal _profitFactor;
    [ObservableProperty] private Brush _pnLColor = Brushes.Gray;
}
