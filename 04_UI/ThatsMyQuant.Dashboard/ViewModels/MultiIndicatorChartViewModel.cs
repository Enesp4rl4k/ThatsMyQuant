using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels;

/// <summary>
/// TradingView-style multi-indicator chart. Renders candles with three indicator
/// overlays (AlphaTrend-like ATR band, PMax-like step line, EMA-21), horizontal
/// support/resistance bands derived from pivot analysis, and buy/sell markers.
///
/// Rendering: pure Canvas + Polyline + Rectangle + ItemsControl (no charting lib).
/// Data flow: Refresh → IHistoricalMarketDataService → compute indicators → bind
/// observable collections → XAML repaints.
/// </summary>
public partial class MultiIndicatorChartViewModel : ObservableObject
{
    private readonly IHistoricalMarketDataService? _history;
    private readonly ILogger<MultiIndicatorChartViewModel>? _logger;

    [ObservableProperty] private string _symbol = "BTC-USDT";
    [ObservableProperty] private string _timeframe = "1h";
    [ObservableProperty] private int _bars = 200;

    [ObservableProperty] private double _chartCanvasWidth = 1100;
    [ObservableProperty] private double _chartCanvasHeight = 460;

    [ObservableProperty] private ObservableCollection<CandleVisual> _candles = new();
    [ObservableProperty] private PointCollection _alphaTrendUpper = new();
    [ObservableProperty] private PointCollection _alphaTrendLower = new();
    [ObservableProperty] private PointCollection _pmaxLine = new();
    [ObservableProperty] private PointCollection _emaLine = new();
    [ObservableProperty] private ObservableCollection<SrBand> _srBands = new();
    [ObservableProperty] private ObservableCollection<TradeMarker> _markers = new();
    [ObservableProperty] private ObservableCollection<PriceAxisLabel> _priceAxisLabels = new();
    [ObservableProperty] private ObservableCollection<TimeAxisLabel> _timeAxisLabels = new();

    [ObservableProperty] private string _statusMessage = "Choose a symbol and click Refresh.";
    [ObservableProperty] private bool _showAlphaTrend = true;
    [ObservableProperty] private bool _showPmax = true;
    [ObservableProperty] private bool _showEma = true;
    [ObservableProperty] private bool _showSrBands = true;
    [ObservableProperty] private bool _showMarkers = true;
    [ObservableProperty] private bool _isRunning;

    public MultiIndicatorChartViewModel(
        IHistoricalMarketDataService? history = null,
        ILogger<MultiIndicatorChartViewModel>? logger = null)
    {
        _history = history;
        _logger = logger;
    }

    // Expose a setter so external code (e.g. paper-trade engine) can push markers.
    public void AddMarker(System.DateTime timeUtc, decimal price, bool isBuy, string label)
    {
        // Markers are mapped to canvas in RebuildAxesAndOverlays.
        _externalMarkers.Add(new ExternalMarker(timeUtc, price, isBuy, label));
        if (_lastCandles is not null) RebuildAxesAndOverlays(_lastCandles);
    }

    private readonly List<ExternalMarker> _externalMarkers = new();
    private IReadOnlyList<Candle>? _lastCandles;
    private record ExternalMarker(System.DateTime Time, decimal Price, bool IsBuy, string Label);

    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        StatusMessage = $"Loading {Symbol} @ {Timeframe} ({Bars} bars)…";
        try
        {
            if (_history is null)
            {
                StatusMessage = "Historical service not available — rendering synthetic candles.";
                _lastCandles = BuildSyntheticCandles(Bars);
            }
            else
            {
                var end = System.DateTime.UtcNow;
                var span = TimeframeSpan(Timeframe);
                var start = end - System.TimeSpan.FromMinutes(span.TotalMinutes * Bars);
                var loaded = await _history.GetHistoricalCandlesAsync(Symbol, Timeframe, start, end)
                    .ConfigureAwait(true);
                if (loaded is null || loaded.Count < 30)
                {
                    StatusMessage = $"Got only {loaded?.Count ?? 0} bars — rendering synthetic baseline.";
                    _lastCandles = BuildSyntheticCandles(Bars);
                }
                else
                {
                    _lastCandles = loaded.Count > Bars ? loaded.Skip(loaded.Count - Bars).ToList() : loaded;
                }
            }
            RebuildAxesAndOverlays(_lastCandles!);
            StatusMessage = $"{Symbol} @ {Timeframe} · {_lastCandles!.Count} bars · last close {_lastCandles[^1].Close:N2}";
        }
        catch (System.Exception ex)
        {
            _logger?.LogError(ex, "Multi-indicator chart refresh failed");
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally { IsRunning = false; }
    }

    partial void OnShowAlphaTrendChanged(bool value) { if (_lastCandles is not null) RebuildAxesAndOverlays(_lastCandles); }
    partial void OnShowPmaxChanged(bool value) { if (_lastCandles is not null) RebuildAxesAndOverlays(_lastCandles); }
    partial void OnShowEmaChanged(bool value) { if (_lastCandles is not null) RebuildAxesAndOverlays(_lastCandles); }
    partial void OnShowSrBandsChanged(bool value) { if (_lastCandles is not null) RebuildAxesAndOverlays(_lastCandles); }
    partial void OnShowMarkersChanged(bool value) { if (_lastCandles is not null) RebuildAxesAndOverlays(_lastCandles); }

    private void RebuildAxesAndOverlays(IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0) return;
        double W = ChartCanvasWidth;
        double H = ChartCanvasHeight;
        double padL = 50, padR = 12, padT = 12, padB = 28;
        double plotW = W - padL - padR;
        double plotH = H - padT - padB;

        decimal minLow = candles.Min(c => c.Low);
        decimal maxHigh = candles.Max(c => c.High);
        decimal vRange = maxHigh - minLow;
        if (vRange == 0m) vRange = 1m;

        double slot = plotW / candles.Count;
        double bodyW = System.Math.Max(2, slot * 0.7);

        double Y(decimal price) => padT + (double)((maxHigh - price) / vRange) * plotH;
        double X(int idx) => padL + slot * idx + slot / 2.0;

        var visuals = new ObservableCollection<CandleVisual>();
        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            double x = X(i);
            bool bull = c.Close >= c.Open;
            visuals.Add(new CandleVisual
            {
                BodyX = x - bodyW / 2,
                BodyY = Y(System.Math.Max(c.Open, c.Close)),
                BodyWidth = bodyW,
                BodyHeight = System.Math.Max(1, System.Math.Abs(Y(c.Open) - Y(c.Close))),
                WickX1 = x, WickX2 = x,
                WickY1 = Y(c.High),
                WickY2 = Y(c.Low),
                FillBrush = bull
                    ? new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B))
                    : new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A)),
                StrokeBrush = bull
                    ? new SolidColorBrush(Color.FromRgb(0x2A, 0x88, 0x47))
                    : new SolidColorBrush(Color.FromRgb(0xC9, 0x4A, 0x4A))
            });
        }
        Candles = visuals;

        // Indicators ----------------------------------------------------------------
        var (atrUp, atrLo, pmax, ema) = ComputeIndicators(candles);
        AlphaTrendUpper = ShowAlphaTrend ? BuildLine(atrUp, candles.Count, X, Y) : new PointCollection();
        AlphaTrendLower = ShowAlphaTrend ? BuildLine(atrLo, candles.Count, X, Y) : new PointCollection();
        PmaxLine = ShowPmax ? BuildLine(pmax, candles.Count, X, Y) : new PointCollection();
        EmaLine = ShowEma ? BuildLine(ema, candles.Count, X, Y) : new PointCollection();

        // S/R bands ----------------------------------------------------------------
        SrBands.Clear();
        if (ShowSrBands)
        {
            var pivots = ComputePivotLevels(candles);
            foreach (var (level, tag) in pivots)
            {
                if (level < minLow || level > maxHigh) continue;
                double y = Y(level);
                var color = tag switch
                {
                    "R2" or "R1" => Color.FromArgb(0x40, 0xF0, 0x5A, 0x5A),
                    "S1" or "S2" => Color.FromArgb(0x40, 0x37, 0xA9, 0x5B),
                    _ => Color.FromArgb(0x35, 0xF0, 0x88, 0x3E)
                };
                SrBands.Add(new SrBand
                {
                    X = padL,
                    Y = y - 3,
                    Width = plotW,
                    Height = 6,
                    BandFill = new SolidColorBrush(color),
                    Label = $"{tag} {level:N2}",
                    LabelX = padL + 4,
                    LabelY = y - 14
                });
            }
        }

        // Markers ------------------------------------------------------------------
        Markers.Clear();
        if (ShowMarkers && _externalMarkers.Count > 0)
        {
            // Map markers to nearest candle index by time.
            for (int m = 0; m < _externalMarkers.Count; m++)
            {
                var em = _externalMarkers[m];
                int bestIdx = 0; long bestDelta = long.MaxValue;
                for (int i = 0; i < candles.Count; i++)
                {
                    var d = System.Math.Abs((candles[i].Timestamp - em.Time).Ticks);
                    if (d < bestDelta) { bestDelta = d; bestIdx = i; }
                }
                double x = X(bestIdx);
                double y = Y(em.Price);
                Markers.Add(new TradeMarker
                {
                    ArrowX = x - 6,
                    ArrowY = em.IsBuy ? y + 6 : y - 18,
                    ArrowFill = em.IsBuy
                        ? new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B))
                        : new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A)),
                    ArrowSymbol = em.IsBuy ? "▲" : "▼",
                    LabelText = $"{em.Label} {em.Price:N2}",
                    LabelX = x + 8,
                    LabelY = em.IsBuy ? y + 8 : y - 22
                });
            }
        }

        // Axis labels --------------------------------------------------------------
        var priceLabels = new ObservableCollection<PriceAxisLabel>();
        for (int i = 0; i <= 5; i++)
        {
            decimal level = minLow + vRange * i / 5;
            priceLabels.Add(new PriceAxisLabel
            {
                X = 4,
                Y = Y(level) - 7,
                Text = level.ToString("N2")
            });
        }
        PriceAxisLabels = priceLabels;

        var timeLabels = new ObservableCollection<TimeAxisLabel>();
        int step = System.Math.Max(1, candles.Count / 6);
        for (int i = 0; i < candles.Count; i += step)
        {
            timeLabels.Add(new TimeAxisLabel
            {
                X = X(i) - 30,
                Y = H - padB + 6,
                Text = candles[i].Timestamp.ToString("MM-dd HH:mm")
            });
        }
        TimeAxisLabels = timeLabels;
    }

    private static PointCollection BuildLine(double[] series, int count, System.Func<int, double> X, System.Func<decimal, double> Y)
    {
        var pts = new PointCollection(count);
        for (int i = 0; i < count; i++)
        {
            if (double.IsNaN(series[i])) continue;
            pts.Add(new Point(X(i), Y((decimal)series[i])));
        }
        return pts;
    }

    /// <summary>Computes AlphaTrend (ATR-banded), PMax (ATR-step), and EMA-21 lines.</summary>
    private static (double[] atrUp, double[] atrLo, double[] pmax, double[] ema)
        ComputeIndicators(IReadOnlyList<Candle> candles)
    {
        int n = candles.Count;
        double[] atrUp = new double[n], atrLo = new double[n], pmax = new double[n], ema = new double[n];
        for (int i = 0; i < n; i++) { atrUp[i] = double.NaN; atrLo[i] = double.NaN; pmax[i] = double.NaN; ema[i] = double.NaN; }

        // 14-bar ATR via Wilder's running average.
        double atr = 0;
        for (int i = 1; i < n; i++)
        {
            double tr = System.Math.Max((double)candles[i].High - (double)candles[i].Low,
                        System.Math.Max(System.Math.Abs((double)candles[i].High - (double)candles[i - 1].Close),
                                        System.Math.Abs((double)candles[i].Low - (double)candles[i - 1].Close)));
            atr = i <= 14 ? (atr * (i - 1) + tr) / i : (atr * 13 + tr) / 14.0;

            // AlphaTrend bands = (high+low)/2 ± 1.5×ATR
            double mid = ((double)candles[i].High + (double)candles[i].Low) / 2.0;
            atrUp[i] = mid + 1.5 * atr;
            atrLo[i] = mid - 1.5 * atr;

            // PMax-ish: trailing stop derived from ATR around close.
            if (i == 1) pmax[i] = (double)candles[i].Close - atr;
            else
            {
                double prev = double.IsNaN(pmax[i - 1]) ? (double)candles[i].Close : pmax[i - 1];
                double upBand = (double)candles[i].Close - 2 * atr;
                double dnBand = (double)candles[i].Close + 2 * atr;
                if ((double)candles[i].Close > prev) pmax[i] = System.Math.Max(prev, upBand);
                else pmax[i] = System.Math.Min(prev > 0 ? prev : dnBand, dnBand);
            }
        }

        // EMA-21 on close.
        double alpha = 2.0 / 22.0;
        ema[0] = (double)candles[0].Close;
        for (int i = 1; i < n; i++)
            ema[i] = alpha * (double)candles[i].Close + (1 - alpha) * ema[i - 1];

        return (atrUp, atrLo, pmax, ema);
    }

    /// <summary>Classic floor-trader pivot levels from the prior 50-bar range.</summary>
    private static List<(decimal level, string tag)> ComputePivotLevels(IReadOnlyList<Candle> candles)
    {
        int look = System.Math.Min(50, candles.Count);
        var slice = candles.Skip(candles.Count - look).ToList();
        decimal hi = slice.Max(c => c.High);
        decimal lo = slice.Min(c => c.Low);
        decimal cl = slice[^1].Close;
        decimal pp = (hi + lo + cl) / 3m;
        decimal r1 = 2 * pp - lo;
        decimal s1 = 2 * pp - hi;
        decimal r2 = pp + (hi - lo);
        decimal s2 = pp - (hi - lo);
        return new List<(decimal, string)>
        {
            (s2, "S2"), (s1, "S1"), (pp, "PP"), (r1, "R1"), (r2, "R2")
        };
    }

    private static System.TimeSpan TimeframeSpan(string tf)
    {
        tf = tf?.Trim().ToLowerInvariant() ?? "1h";
        return tf switch
        {
            "1m" => System.TimeSpan.FromMinutes(1),
            "5m" => System.TimeSpan.FromMinutes(5),
            "15m" => System.TimeSpan.FromMinutes(15),
            "30m" => System.TimeSpan.FromMinutes(30),
            "1h" => System.TimeSpan.FromHours(1),
            "4h" => System.TimeSpan.FromHours(4),
            "1d" => System.TimeSpan.FromDays(1),
            _ => System.TimeSpan.FromHours(1)
        };
    }

    private static IReadOnlyList<Candle> BuildSyntheticCandles(int n)
    {
        var rng = new System.Random(42);
        var list = new List<Candle>(n);
        decimal price = 50_000m;
        var t = System.DateTime.UtcNow.AddHours(-n);
        for (int i = 0; i < n; i++)
        {
            decimal drift = (decimal)(rng.NextDouble() - 0.48) * 200m;
            decimal open = price;
            decimal close = price + drift;
            decimal hi = System.Math.Max(open, close) + (decimal)rng.NextDouble() * 80m;
            decimal lo = System.Math.Min(open, close) - (decimal)rng.NextDouble() * 80m;
            list.Add(new Candle { Timestamp = t, Open = open, High = hi, Low = lo, Close = close, Volume = 100m + (decimal)rng.NextDouble() * 50m });
            price = close;
            t = t.AddHours(1);
        }
        return list;
    }
}

public partial class CandleVisual : ObservableObject
{
    [ObservableProperty] private double _bodyX, _bodyY, _bodyWidth, _bodyHeight;
    [ObservableProperty] private double _wickX1, _wickX2, _wickY1, _wickY2;
    [ObservableProperty] private Brush _fillBrush = new SolidColorBrush(Colors.LimeGreen);
    [ObservableProperty] private Brush _strokeBrush = new SolidColorBrush(Colors.Green);
}

public partial class SrBand : ObservableObject
{
    [ObservableProperty] private double _x, _y, _width, _height;
    [ObservableProperty] private Brush _bandFill = new SolidColorBrush(Color.FromArgb(0x40, 0xF0, 0x88, 0x3E));
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private double _labelX, _labelY;
}

public partial class TradeMarker : ObservableObject
{
    [ObservableProperty] private double _arrowX, _arrowY;
    [ObservableProperty] private Brush _arrowFill = new SolidColorBrush(Colors.Lime);
    [ObservableProperty] private string _arrowSymbol = "▲";
    [ObservableProperty] private string _labelText = "";
    [ObservableProperty] private double _labelX, _labelY;
}

public partial class PriceAxisLabel : ObservableObject
{
    [ObservableProperty] private double _x, _y;
    [ObservableProperty] private string _text = "";
}

public partial class TimeAxisLabel : ObservableObject
{
    [ObservableProperty] private double _x, _y;
    [ObservableProperty] private string _text = "";
}
