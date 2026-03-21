using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class CandleViewModel : ObservableObject
{
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _wickX;
    [ObservableProperty] private double _bodyY;
    [ObservableProperty] private double _bodyHeight;
    [ObservableProperty] private double _wickY1;
    [ObservableProperty] private double _wickY2;
    [ObservableProperty] private double _width;
    [ObservableProperty] private Brush _color = Brushes.Gray;
}
