using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MonthlyPerformanceViewModel : ObservableObject
{
    [ObservableProperty] private string _monthName = string.Empty;
    [ObservableProperty] private decimal _pnL = 0;
    [ObservableProperty] private string _toolTip = string.Empty;
    [ObservableProperty] private Brush _color = Brushes.Gray;
    [ObservableProperty] private double _opacity = 1.0;

    public void Update(string month, decimal pnl, Brush color, double opacity)
    {
        MonthName = month;
        PnL = pnl;
        Color = color;
        Opacity = opacity;
        ToolTip = $"{month}: {pnl:+0.00;-0.00;0.00}%";
    }
}
