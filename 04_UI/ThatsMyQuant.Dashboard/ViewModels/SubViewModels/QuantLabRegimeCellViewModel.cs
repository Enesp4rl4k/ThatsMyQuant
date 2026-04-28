using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public sealed class QuantLabRegimeCellViewModel
{
    public QuantLabRegimeCellViewModel(double x, double width, Brush brush, string tooltip)
    {
        X = x;
        Width = width;
        Brush = brush;
        Tooltip = tooltip;
    }

    public double X { get; }
    public double Width { get; }
    public Brush Brush { get; }
    public string Tooltip { get; }
}
