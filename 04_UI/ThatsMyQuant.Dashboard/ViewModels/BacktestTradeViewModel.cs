using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Windows.Media;
using ThatsMyQuant.Domain.Enums;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class BacktestTradeViewModel : ObservableObject
{
    [ObservableProperty] private DateTime _entryTime;
    [ObservableProperty] private DateTime _exitTime;
    [ObservableProperty] private string _side = "";
    [ObservableProperty] private string _strategyTag = "";
    [ObservableProperty] private string _regimeTag = "";
    [ObservableProperty] private string _exitReason = "";
    [ObservableProperty] private decimal _entryPrice;
    [ObservableProperty] private decimal _exitPrice;
    [ObservableProperty] private decimal _pnL;
    [ObservableProperty] private decimal _pnLPercent;
    [ObservableProperty] private Brush _color = Brushes.Gray;
}
