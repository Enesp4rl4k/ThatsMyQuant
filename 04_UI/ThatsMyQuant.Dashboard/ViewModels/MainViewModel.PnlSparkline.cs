using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ThatsMyQuant.Dashboard.ViewModels;

// Intraday P&L sparkline — 60 samples @ 5s interval = last 5 minutes of combined PnL.
public partial class MainViewModel
{
    private const int SparklineCapacity = 60;
    private const double SparklineWidth = 90.0;
    private const double SparklineHeight = 22.0;

    private readonly List<double> _pnlSamples = new(SparklineCapacity);

    public string PnlSparklinePoints { get; private set; } = string.Empty;
    public string PnlSparklineColor { get; private set; } = "#00FFBB";

    internal void SamplePnlForSparkline()
    {
        var pnl = (double)(PortfolioView.RealizedPnL + PortfolioView.UnrealizedPnL);
        _pnlSamples.Add(pnl);
        while (_pnlSamples.Count > SparklineCapacity)
        {
            _pnlSamples.RemoveAt(0);
        }

        RebuildSparklinePoints();
    }

    private void RebuildSparklinePoints()
    {
        if (_pnlSamples.Count < 2)
        {
            PnlSparklinePoints = string.Empty;
            OnPropertyChanged(nameof(PnlSparklinePoints));
            return;
        }

        var min = _pnlSamples.Min();
        var max = _pnlSamples.Max();
        var span = max - min;
        if (span <= 0.0000001)
        {
            span = 1.0;
        }

        var stepX = SparklineWidth / Math.Max(1, _pnlSamples.Count - 1);
        var points = new List<string>(_pnlSamples.Count);
        for (int i = 0; i < _pnlSamples.Count; i++)
        {
            var x = i * stepX;
            var y = SparklineHeight - ((_pnlSamples[i] - min) / span) * SparklineHeight;
            points.Add($"{x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        PnlSparklinePoints = string.Join(" ", points);

        // Color reflects last delta direction.
        var last = _pnlSamples[^1];
        var first = _pnlSamples[0];
        PnlSparklineColor = last >= first ? "#00FFBB" : "#FF3366";

        OnPropertyChanged(nameof(PnlSparklinePoints));
        OnPropertyChanged(nameof(PnlSparklineColor));
    }
}
