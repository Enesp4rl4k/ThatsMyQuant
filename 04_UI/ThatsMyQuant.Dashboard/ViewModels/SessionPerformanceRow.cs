using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class SessionPerformanceRow : ObservableObject
{
    [ObservableProperty] private string _sessionName = "";
    [ObservableProperty] private int _trades;
    [ObservableProperty] private double _winRate;
    [ObservableProperty] private double _profitFactor;
    [ObservableProperty] private decimal _totalPnL;
    [ObservableProperty] private Brush _pnlColor = Brushes.Gray;
}
