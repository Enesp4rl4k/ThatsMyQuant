using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Media;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Dashboard.ViewModels;

/// <summary>
/// Pair-candidate discovery UI. Takes a symbol basket, pulls historical candles for
/// each, time-aligns the close-price series, and runs <see cref="PairsDiscoveryService"/>
/// to emit ranked cointegrating pairs with half-life, Hurst, correlation and composite
/// quality score.
/// </summary>
public partial class PairsDiscoveryViewModel : ObservableObject
{
    private readonly PairsDiscoveryService? _discovery;
    private readonly IHistoricalMarketDataService? _history;
    private readonly ILogger<PairsDiscoveryViewModel>? _logger;

    [ObservableProperty] private string _symbolsCsv =
        "BTC-USDT, ETH-USDT, SOL-USDT, AVAX-USDT, LINK-USDT, LTC-USDT, DOGE-USDT, XRP-USDT, ADA-USDT, ATOM-USDT, DOT-USDT, TRX-USDT, XLM-USDT, AAVE-USDT";
    [ObservableProperty] private string _timeframe = "4h";
    [ObservableProperty] private int _daysBack = 365;
    [ObservableProperty] private int _topN = 20;

    [ObservableProperty] private ObservableCollection<PairCandidateRow> _candidates = new();
    [ObservableProperty] private string _statusMessage = "Configure basket + timeframe, then click Scan.";
    [ObservableProperty] private Brush _statusColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private int _symbolsTested;
    [ObservableProperty] private int _pairsAdmitted;

    [ObservableProperty] private PairCandidateRow? _selectedPair;
    [ObservableProperty] private System.Windows.Media.PointCollection _spreadPoints = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _zScorePoints = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _zScoreUpperBand = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _zScoreLowerBand = new();
    [ObservableProperty] private System.Windows.Media.PointCollection _zScoreZeroLine = new();
    [ObservableProperty] private double _spreadCanvasWidth = 900;
    [ObservableProperty] private double _spreadCanvasHeight = 180;
    [ObservableProperty] private string _selectedPairTitle = "Select a pair row to inspect its spread dynamics.";
    [ObservableProperty] private string _selectedPairStats = "";
    [ObservableProperty] private bool _hasSpreadChart;

    // Cache raw closes from the last scan so clicking a row doesn't re-download.
    private readonly Dictionary<string, IReadOnlyList<decimal>> _closeCache = new(StringComparer.OrdinalIgnoreCase);

    public PairsDiscoveryViewModel(
        PairsDiscoveryService? discovery = null,
        IHistoricalMarketDataService? history = null,
        ILogger<PairsDiscoveryViewModel>? logger = null)
    {
        _discovery = discovery;
        _history = history;
        _logger = logger;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_discovery is null || _history is null)
        {
            StatusMessage = "PairsDiscoveryService or IHistoricalMarketDataService not wired in DI.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
            return;
        }
        if (IsScanning) return;

        var symbols = SymbolsCsv.Split(new[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (symbols.Length < 2)
        {
            StatusMessage = "Need at least 2 symbols.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
            return;
        }

        IsScanning = true;
        StatusMessage = $"Fetching candles for {symbols.Length} symbols × {DaysBack}d @ {Timeframe}...";
        StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
        Candidates.Clear();

        try
        {
            var end = DateTime.UtcNow;
            var start = end.AddDays(-DaysBack);

            // Fetch all series in parallel.
            var fetchTasks = symbols.Select(async s =>
            {
                try
                {
                    var cs = await _history.GetHistoricalCandlesAsync(s, Timeframe, start, end);
                    return new SeriesFetch(s, cs.Select(c => c.Close).ToList());
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Fetch failed for {S}", s);
                    return new SeriesFetch(s, null);
                }
            }).ToArray();
            var fetched = await Task.WhenAll(fetchTasks);

            var seriesMap = fetched
                .Where(f => f.Closes is not null && f.Closes.Count > 0)
                .ToDictionary(f => f.Symbol, f => (IReadOnlyList<decimal>)f.Closes!, StringComparer.OrdinalIgnoreCase);

            // Align to common minimum length (trim tail to shortest).
            int minLen = seriesMap.Values.Min(v => v.Count);
            var aligned = seriesMap.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<decimal>)kv.Value.Skip(kv.Value.Count - minLen).ToList(),
                StringComparer.OrdinalIgnoreCase);

            // Cache for spread-chart reuse.
            _closeCache.Clear();
            foreach (var kv in aligned) _closeCache[kv.Key] = kv.Value;

            StatusMessage = $"Scanning {aligned.Count} symbols ({aligned.Count * (aligned.Count - 1) / 2} ordered pairs, both directions) — {minLen} aligned bars...";

            var results = _discovery.Discover(aligned, TopN);

            SymbolsTested = aligned.Count;
            PairsAdmitted = results.Count;

            foreach (var c in results)
            {
                Candidates.Add(new PairCandidateRow
                {
                    SymbolY = c.SymbolY,
                    SymbolX = c.SymbolX,
                    PairLabel = $"{c.SymbolY} / {c.SymbolX}",
                    QualityScore = c.QualityScore,
                    HedgeRatio = c.Diagnostics.HedgeRatio,
                    HalfLife = c.Diagnostics.HalfLife,
                    Hurst = c.Diagnostics.HurstExponent,
                    Correlation = c.Diagnostics.Correlation,
                    AdfTStat = c.Diagnostics.AdfTStatistic,
                    QualityBrush = c.QualityScore switch
                    {
                        >= 70 => new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B)),
                        >= 50 => new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E)),
                        _ => new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E))
                    }
                });
            }

            StatusMessage = $"Scan complete: {aligned.Count} symbols → {results.Count} cointegrated pairs admitted (top {TopN}). Ranked by composite quality (0-100).";
            StatusColor = results.Count > 0
                ? new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B))
                : new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Pairs discovery scan failed");
            StatusMessage = $"Scan failed: {ex.Message}";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
        }
        finally { IsScanning = false; }
    }

    partial void OnSelectedPairChanged(PairCandidateRow? value)
    {
        if (value is null) { HasSpreadChart = false; return; }
        BuildSpreadChart(value);
    }

    /// <summary>Renders spread + rolling z-score for the selected pair using cached close series.</summary>
    private void BuildSpreadChart(PairCandidateRow pair)
    {
        if (!_closeCache.TryGetValue(pair.SymbolY, out var y) ||
            !_closeCache.TryGetValue(pair.SymbolX, out var x))
        {
            SelectedPairTitle = $"{pair.PairLabel} — close series not cached (rescan first).";
            HasSpreadChart = false;
            return;
        }

        int n = Math.Min(y.Count, x.Count);
        if (n < 60) { HasSpreadChart = false; return; }

        decimal beta = pair.HedgeRatio;
        // Spread = Y - β·X
        var spread = new double[n];
        for (int i = 0; i < n; i++) spread[i] = (double)(y[i] - beta * x[i]);

        // Rolling z-score — 30-bar window.
        int win = 30;
        var zs = new double[n];
        for (int i = 0; i < n; i++)
        {
            int from = Math.Max(0, i - win + 1);
            int len = i - from + 1;
            double mean = 0, m2 = 0;
            for (int k = from; k <= i; k++)
            {
                double d = spread[k] - mean;
                mean += d / (k - from + 1);
                m2 += d * (spread[k] - mean);
            }
            double std = len > 1 ? Math.Sqrt(m2 / (len - 1)) : 0.0;
            zs[i] = std > 1e-9 ? (spread[i] - mean) / std : 0.0;
        }

        double W = SpreadCanvasWidth;
        double H = SpreadCanvasHeight;
        double pad = 10;

        // Z-score canvas: clamp to [-4, 4].
        double zMin = -4, zMax = 4;
        var zPoints = new System.Windows.Media.PointCollection(n);
        var zZero = new System.Windows.Media.PointCollection(2);
        var zUp = new System.Windows.Media.PointCollection(2);
        var zDown = new System.Windows.Media.PointCollection(2);
        double MapZ(double z) => H - pad - ((z - zMin) / (zMax - zMin)) * (H - 2 * pad);
        for (int i = 0; i < n; i++)
        {
            double xp = pad + (W - 2 * pad) * i / Math.Max(1, n - 1);
            double yp = MapZ(Math.Clamp(zs[i], zMin, zMax));
            zPoints.Add(new System.Windows.Point(xp, yp));
        }
        zZero.Add(new System.Windows.Point(pad, MapZ(0))); zZero.Add(new System.Windows.Point(W - pad, MapZ(0)));
        zUp.Add(new System.Windows.Point(pad, MapZ(2))); zUp.Add(new System.Windows.Point(W - pad, MapZ(2)));
        zDown.Add(new System.Windows.Point(pad, MapZ(-2))); zDown.Add(new System.Windows.Point(W - pad, MapZ(-2)));

        // Spread curve — normalized to canvas H.
        double sMin = spread.Min(), sMax = spread.Max();
        double sRange = sMax - sMin; if (sRange == 0) sRange = 1;
        var sPoints = new System.Windows.Media.PointCollection(n);
        for (int i = 0; i < n; i++)
        {
            double xp = pad + (W - 2 * pad) * i / Math.Max(1, n - 1);
            double yp = H - pad - ((spread[i] - sMin) / sRange) * (H - 2 * pad);
            sPoints.Add(new System.Windows.Point(xp, yp));
        }

        SpreadPoints = sPoints;
        ZScorePoints = zPoints;
        ZScoreZeroLine = zZero;
        ZScoreUpperBand = zUp;
        ZScoreLowerBand = zDown;

        int entryEvents = 0, exitEvents = 0;
        for (int i = 1; i < n; i++)
        {
            if ((zs[i - 1] < 2 && zs[i] >= 2) || (zs[i - 1] > -2 && zs[i] <= -2)) entryEvents++;
            if ((zs[i - 1] > 0.3 && zs[i] <= 0.3) || (zs[i - 1] < -0.3 && zs[i] >= -0.3)) exitEvents++;
        }

        SelectedPairTitle = $"{pair.PairLabel} · β={beta:F4} · HL={pair.HalfLife:F1} · Hurst={pair.Hurst:F3} · ρ={pair.Correlation:F3}";
        SelectedPairStats = $"Spread range [{sMin:F2}, {sMax:F2}] · |z|max={zs.Max(Math.Abs):F2} · ±2σ entry signals={entryEvents} · mean-reversion exits={exitEvents}";
        HasSpreadChart = true;
    }
}

file sealed record SeriesFetch(string Symbol, List<decimal>? Closes);

public partial class PairCandidateRow : ObservableObject
{
    [ObservableProperty] private string _symbolY = "";
    [ObservableProperty] private string _symbolX = "";
    [ObservableProperty] private string _pairLabel = "";
    [ObservableProperty] private double _qualityScore;
    [ObservableProperty] private decimal _hedgeRatio;
    [ObservableProperty] private double _halfLife;
    [ObservableProperty] private double _hurst;
    [ObservableProperty] private double _correlation;
    [ObservableProperty] private double _adfTStat;
    [ObservableProperty] private Brush _qualityBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
}
