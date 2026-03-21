using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class IndicatorDetailViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _category = "General"; // Used for logic tree grouping
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private Brush _color = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x94, 0x9E));
}
