using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class ModelPerformanceRow : ObservableObject
{
    [ObservableProperty] private string _modelVersion = "";
    [ObservableProperty] private int _signals;
    [ObservableProperty] private int _closedTrades;
    [ObservableProperty] private double _winRate;
    [ObservableProperty] private decimal _expectedValue;
    [ObservableProperty] private double _driftRate;
    [ObservableProperty] private double _vetoRate;
    [ObservableProperty] private double _sizeTrimRate;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private Brush _statusColor = Brushes.Gray;
}
