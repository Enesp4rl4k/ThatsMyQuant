using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class ExitPolicyDriftRow : ObservableObject
{
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _strategyTag = "";
    [ObservableProperty] private string _regimeTag = "";
    [ObservableProperty] private string _sessionTag = "";
    [ObservableProperty] private string _exitPolicyTag = "";
    [ObservableProperty] private int _paperTrades;
    [ObservableProperty] private int _liveTrades;
    [ObservableProperty] private double _driftScore;
    [ObservableProperty] private double _paperWinRate;
    [ObservableProperty] private double _liveWinRate;
    [ObservableProperty] private double _paperProfitFactor;
    [ObservableProperty] private double _liveProfitFactor;
    [ObservableProperty] private decimal _paperExpectedValue;
    [ObservableProperty] private decimal _liveExpectedValue;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private Brush _severityColor = Brushes.Gray;
}
