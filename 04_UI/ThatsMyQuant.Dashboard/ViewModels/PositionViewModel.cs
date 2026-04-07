using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class PositionViewModel : ObservableObject
{
    private static readonly Brush LongBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush ShortBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));
    private static readonly Brush ProfitBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush LossBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));
    private static readonly Brush AcceptedBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush PartialBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly Brush ClosingBrush = new SolidColorBrush(Color.FromRgb(0x49, 0x9F, 0xF8));
    private static readonly Brush ClosedBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly Brush RejectedBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));

    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _side = "LONG"; // LONG or SHORT
    [ObservableProperty] private decimal _size;
    [ObservableProperty] private decimal _entryPrice;
    [ObservableProperty] private decimal _markPrice;
    [ObservableProperty] private decimal _unrealisedPnL;
    [ObservableProperty] private double _roe;
    [ObservableProperty] private string _leverage = "--";
    [ObservableProperty] private string _lifecycleStatus = "Active";
    [ObservableProperty] private string _exitPolicyTag = "TrendRunner";
    [ObservableProperty] private decimal _stopLoss;
    [ObservableProperty] private decimal _takeProfit;
    [ObservableProperty] private decimal _remainingQuantity;
    
    public Brush SideColor => Side == "LONG" ? LongBrush : ShortBrush;

    public Brush PnLColor => UnrealisedPnL >= 0 ? ProfitBrush : LossBrush;

    public Brush LifecycleColor => LifecycleStatus switch
    {
        "Submitted" or "Accepted" => AcceptedBrush,
        "Partial Fill" or "Partial Exit" => PartialBrush,
        "Active" => ActiveBrush,
        "Closing" => ClosingBrush,
        "Closed" or "Cancelled" => ClosedBrush,
        "Rejected" or "Failed" => RejectedBrush,
        _ => ClosedBrush
    };
}
