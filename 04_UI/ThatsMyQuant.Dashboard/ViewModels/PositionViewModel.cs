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
    [ObservableProperty] private string _lifecycleStatus = "Active";
    [ObservableProperty] private string _exitPolicyTag = "TrendRunner";
    [ObservableProperty] private decimal _stopLoss;
    [ObservableProperty] private decimal _takeProfit;
    [ObservableProperty] private decimal _remainingQuantity;
    
    public Brush SideColor => Side == "LONG" ? 
        new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)) : 
        new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));

    public Brush PnLColor => UnrealisedPnL >= 0 ? 
        new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)) : 
        new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));

    public Brush LifecycleColor => LifecycleStatus switch
    {
        "Submitted" or "Accepted" => new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E)),
        "Partial Exit" => new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22)),
        "Closed" => new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
        _ => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50))
    };
}
