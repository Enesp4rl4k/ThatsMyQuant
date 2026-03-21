using CommunityToolkit.Mvvm.ComponentModel;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class ChartGridLineViewModel : ObservableObject
{
    [ObservableProperty] private double _x1;
    [ObservableProperty] private double _y1;
    [ObservableProperty] private double _x2;
    [ObservableProperty] private double _y2;
}
