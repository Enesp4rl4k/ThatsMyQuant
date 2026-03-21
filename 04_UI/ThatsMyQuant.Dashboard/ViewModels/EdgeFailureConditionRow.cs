using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class EdgeFailureConditionRow : ObservableObject
{
    [ObservableProperty] private string _condition = "";
    [ObservableProperty] private string _executionModeScope = "";
    [ObservableProperty] private string _promotionStageScope = "";
    [ObservableProperty] private int _failures;
    [ObservableProperty] private double _failureRate;
    [ObservableProperty] private decimal _averageSlippagePercent;
    [ObservableProperty] private double _averageLatencyMs;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private Brush _severityColor = Brushes.Gray;
}
