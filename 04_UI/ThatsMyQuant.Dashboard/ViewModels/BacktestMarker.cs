using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class BacktestMarker : ObservableObject
{
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private Brush _color = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _direction = "Buy"; // Buy or Sell
    [ObservableProperty] private string _toolTip = "";
}
