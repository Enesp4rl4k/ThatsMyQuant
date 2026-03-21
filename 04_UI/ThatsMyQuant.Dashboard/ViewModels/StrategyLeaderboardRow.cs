using CommunityToolkit.Mvvm.ComponentModel;

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
}
