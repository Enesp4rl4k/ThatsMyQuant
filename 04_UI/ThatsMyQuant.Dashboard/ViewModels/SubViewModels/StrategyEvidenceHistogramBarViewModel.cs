using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public sealed class StrategyEvidenceHistogramBarViewModel
{
    public StrategyEvidenceHistogramBarViewModel(string label, double barHeight, int count, Brush barBrush)
    {
        Label = label;
        BarHeight = barHeight;
        Count = count;
        BarBrush = barBrush;
    }

    public string Label { get; }
    public double BarHeight { get; }
    public int Count { get; }
    public Brush BarBrush { get; }
}
