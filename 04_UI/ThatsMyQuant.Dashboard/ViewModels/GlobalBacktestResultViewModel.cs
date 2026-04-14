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
    [ObservableProperty] private double _sharpeRatio;
    [ObservableProperty] private double _sortinoRatio;
    [ObservableProperty] private decimal _maxDrawdownPercent;
    [ObservableProperty] private double _averageRR;
    [ObservableProperty] private int _maxConsecutiveLosses;
    [ObservableProperty] private decimal _startingCapital;
    [ObservableProperty] private decimal _finalEquity;
    [ObservableProperty] private string _returnPercent = "--";
    [ObservableProperty] private Brush _pnLColor = Brushes.Gray;
    [ObservableProperty] private bool _backtestPass;
}
