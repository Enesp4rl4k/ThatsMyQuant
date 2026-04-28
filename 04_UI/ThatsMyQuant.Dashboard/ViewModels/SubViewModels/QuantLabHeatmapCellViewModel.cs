using System.Collections.Generic;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public sealed class QuantLabHeatmapCellViewModel
{
    public QuantLabHeatmapCellViewModel(string label, double value, Brush brush, string tooltip)
    {
        Label = label;
        Value = value;
        Brush = brush;
        Tooltip = tooltip;
    }

    public string Label { get; }
    public double Value { get; }
    public Brush Brush { get; }
    public string Tooltip { get; }
}

public sealed class QuantLabHeatmapRowViewModel
{
    public QuantLabHeatmapRowViewModel(string rowLabel, IReadOnlyList<QuantLabHeatmapCellViewModel> cells)
    {
        RowLabel = rowLabel;
        Cells = cells;
    }

    public string RowLabel { get; }
    public IReadOnlyList<QuantLabHeatmapCellViewModel> Cells { get; }
}
