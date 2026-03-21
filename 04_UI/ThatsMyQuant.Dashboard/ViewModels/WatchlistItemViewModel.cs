using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class WatchlistItemViewModel : ObservableObject
{
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private decimal _price;
    [ObservableProperty] private double _changePercent;
    public Brush Color => ChangePercent >= 0 ? GreenBrush : RedBrush;
    public string ChangeText => $"% {(ChangePercent >= 0 ? "+" : "")}{ChangePercent:F2}";

    private static readonly Brush GreenBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xBF, 0xA5));
    private static readonly Brush RedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x52, 0x52));
}
