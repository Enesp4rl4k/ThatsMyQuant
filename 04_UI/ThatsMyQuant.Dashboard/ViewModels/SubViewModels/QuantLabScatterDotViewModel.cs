using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public sealed class QuantLabScatterDotViewModel
{
    public QuantLabScatterDotViewModel(double x, double y, Brush brush, string tooltip)
    {
        X = x;
        Y = y;
        Brush = brush;
        Tooltip = tooltip;
    }

    public double X { get; }
    public double Y { get; }
    public Brush Brush { get; }
    public string Tooltip { get; }
}
