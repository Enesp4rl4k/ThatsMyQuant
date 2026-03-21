using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class PositionViewModel : ObservableObject
{
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _side = "LONG"; // LONG or SHORT
    [ObservableProperty] private decimal _size;
    [ObservableProperty] private decimal _entryPrice;
    [ObservableProperty] private decimal _markPrice;
    [ObservableProperty] private decimal _unrealisedPnL;
    [ObservableProperty] private double _roe;
    [ObservableProperty] private string _leverage = "20x";
    
    public Brush SideColor => Side == "LONG" ? 
        new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)) : 
        new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));

    public Brush PnLColor => UnrealisedPnL >= 0 ? 
        new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)) : 
        new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));
}
