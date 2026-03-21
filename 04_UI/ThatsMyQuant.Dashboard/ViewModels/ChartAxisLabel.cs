using CommunityToolkit.Mvvm.ComponentModel;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class ChartAxisLabel : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
}
