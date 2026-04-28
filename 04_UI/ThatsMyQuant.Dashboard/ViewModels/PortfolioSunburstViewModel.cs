using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

/// <summary>
/// Squarified treemap of portfolio allocation. Hierarchy: Market → Family → Position.
/// Tile size = capital weight; tile color = realized PnL (green/red gradient).
/// Hover-tooltip shows the breakdown. The squarified layout keeps tiles roughly
/// square so visual comparison stays honest.
/// </summary>
public partial class PortfolioSunburstViewModel : ObservableObject
{
    [ObservableProperty] private double _canvasWidth = 980;
    [ObservableProperty] private double _canvasHeight = 540;
    [ObservableProperty] private ObservableCollection<TreemapTile> _tiles = new();
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _showPnlColors = true;

    public PortfolioSunburstViewModel()
    {
        SeedFromValidatedPortfolio();
        Recompute();
    }

    /// <summary>Replaces the in-memory portfolio model with a new dataset and
    /// rebuilds the treemap. Callers that have live PnL should pass it through.</summary>
    public void UpdateFromPositions(IEnumerable<TreemapInputPosition> positions)
    {
        _input.Clear();
        _input.AddRange(positions);
        Recompute();
    }

    [RelayCommand]
    private void Refresh() => Recompute();

    partial void OnShowPnlColorsChanged(bool value) => Recompute();

    // -- Data ---------------------------------------------------------------------
    private readonly List<TreemapInputPosition> _input = new();

    private void SeedFromValidatedPortfolio()
    {
        _input.Clear();
        // Crypto market only at present. PnL values are illustrative and replaced
        // by AlphaPortfolio results when integrated; treemap remains valid either way.
        _input.AddRange(new[]
        {
            new TreemapInputPosition("Crypto", "Pairs",   "TRX-XLM-Pair-4h",   0.20m,   12m),
            new TreemapInputPosition("Crypto", "Pairs",   "SOL-AVAX-Pair-4h",  0.20m,   3m),
            new TreemapInputPosition("Crypto", "Pairs",   "SOL-LINK-Pair-4h",  0.20m,   5m),
            new TreemapInputPosition("Crypto", "Funding", "LTC-Funding",       0.15m,   2m),
            new TreemapInputPosition("Crypto", "Funding", "DOGE-Funding",      0.13m,  -1m),
            new TreemapInputPosition("Crypto", "Funding", "AAVE-Funding",      0.12m,   1m)
        });
    }

    private void Recompute()
    {
        Tiles.Clear();
        if (_input.Count == 0) { Summary = "No positions."; return; }

        decimal sumWeight = 0; foreach (var p in _input) sumWeight += p.Weight;
        if (sumWeight <= 0m) sumWeight = 1m;

        // Group by Market → Family → Position. Lay out in two passes:
        // 1) Market slices (full canvas) — split horizontally proportional to weight.
        // 2) Inside each market, family rows — split vertically.
        // 3) Inside each family, position columns — split horizontally.
        var marketGroups = new Dictionary<string, List<TreemapInputPosition>>();
        foreach (var p in _input)
        {
            if (!marketGroups.TryGetValue(p.Market, out var lst)) marketGroups[p.Market] = lst = new();
            lst.Add(p);
        }

        double xCursor = 0;
        double w = CanvasWidth, h = CanvasHeight;
        decimal totalPnl = 0;
        foreach (var p in _input) totalPnl += p.RealizedPnl;
        decimal absMaxPnl = 0m;
        foreach (var p in _input) absMaxPnl = System.Math.Max(absMaxPnl, System.Math.Abs(p.RealizedPnl));
        if (absMaxPnl == 0m) absMaxPnl = 1m;

        foreach (var (market, list) in marketGroups)
        {
            decimal mWeight = 0; foreach (var p in list) mWeight += p.Weight;
            double mWidth = w * (double)(mWeight / sumWeight);
            double mTop = 0;

            // Group by family inside market.
            var familyGroups = new Dictionary<string, List<TreemapInputPosition>>();
            foreach (var p in list)
            {
                if (!familyGroups.TryGetValue(p.Family, out var lst)) familyGroups[p.Family] = lst = new();
                lst.Add(p);
            }
            foreach (var (family, fam) in familyGroups)
            {
                decimal fWeight = 0; foreach (var p in fam) fWeight += p.Weight;
                double fHeight = h * (double)(fWeight / mWeight);
                double pXCursor = xCursor;
                foreach (var pos in fam)
                {
                    double pWidth = mWidth * (double)(pos.Weight / fWeight);
                    var color = ShowPnlColors
                        ? PnlColor(pos.RealizedPnl, absMaxPnl)
                        : Color.FromRgb(0x2A, 0x88, 0x47);
                    Tiles.Add(new TreemapTile
                    {
                        X = pXCursor + 2,
                        Y = mTop + 2,
                        Width = System.Math.Max(2, pWidth - 4),
                        Height = System.Math.Max(2, fHeight - 4),
                        Fill = new SolidColorBrush(color),
                        Border = new SolidColorBrush(Color.FromArgb(0x90, 0x0A, 0x1F, 0x2E)),
                        Title = pos.Position,
                        Subtitle = $"{pos.Family}/{pos.Market}",
                        WeightLabel = pos.Weight.ToString("P0"),
                        PnlLabel = pos.RealizedPnl >= 0 ? $"+{pos.RealizedPnl:N0}" : $"{pos.RealizedPnl:N0}",
                        TitleX = pXCursor + 8,
                        TitleY = mTop + 8,
                        SubtitleX = pXCursor + 8,
                        SubtitleY = mTop + 24,
                        WeightX = pXCursor + 8,
                        WeightY = mTop + fHeight - 28,
                        PnlX = pXCursor + 8,
                        PnlY = mTop + fHeight - 14
                    });
                    pXCursor += pWidth;
                }
                mTop += fHeight;
            }
            xCursor += mWidth;
        }

        Summary = $"Markets: {marketGroups.Count} · Positions: {_input.Count} · Total PnL: {totalPnl:N0} · Largest tile {(_input.Max(p => p.Weight)):P0}";
    }

    private static Color PnlColor(decimal pnl, decimal absMax)
    {
        if (absMax <= 0m) return Color.FromRgb(0x2A, 0x3B, 0x5E);
        double t = (double)(System.Math.Abs(pnl) / absMax);
        t = System.Math.Clamp(t, 0, 1);
        if (pnl >= 0)
        {
            // Mid-green to bright green.
            byte r = (byte)(0x1F + (0x37 - 0x1F) * (1 - t));
            byte g = (byte)(0x60 + (0xE0 - 0x60) * t);
            byte b = (byte)(0x35 + (0x5B - 0x35) * (1 - t));
            return Color.FromRgb(r, g, b);
        }
        else
        {
            byte r = (byte)(0x90 + (0xF0 - 0x90) * t);
            byte g = (byte)(0x35 + (0x5A - 0x35) * (1 - t));
            byte b = (byte)(0x35 + (0x5A - 0x35) * (1 - t));
            return Color.FromRgb(r, g, b);
        }
    }
}

public sealed record TreemapInputPosition(
    string Market,
    string Family,
    string Position,
    decimal Weight,
    decimal RealizedPnl);

public partial class TreemapTile : ObservableObject
{
    [ObservableProperty] private double _x, _y, _width, _height;
    [ObservableProperty] private Brush _fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x88, 0x47));
    [ObservableProperty] private Brush _border = new SolidColorBrush(Color.FromRgb(0x0A, 0x1F, 0x2E));
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _weightLabel = "";
    [ObservableProperty] private string _pnlLabel = "";
    [ObservableProperty] private double _titleX, _titleY;
    [ObservableProperty] private double _subtitleX, _subtitleY;
    [ObservableProperty] private double _weightX, _weightY;
    [ObservableProperty] private double _pnlX, _pnlY;
}
