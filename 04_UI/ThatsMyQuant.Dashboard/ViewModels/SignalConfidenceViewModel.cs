using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels;

/// <summary>
/// Six-axis radar (spider) chart for visualising signal confidence: Momentum,
/// Trend Strength, Volume Ratio, Volatility, Sentiment, AlphaTrend Alignment.
/// A polygon that fills outward in all six dimensions = "all-in" conviction.
///
/// Self-contained: a Refresh command pulls recent candles for the selected
/// symbol, derives 6 normalized [0,1] scores from technical metrics, and
/// repaints the radar. Background concentric rings (25/50/75/100%) provide
/// reference scale; per-axis labels float at the outer ring.
/// </summary>
public partial class SignalConfidenceViewModel : ObservableObject
{
    private readonly IHistoricalMarketDataService? _history;
    private readonly ILogger<SignalConfidenceViewModel>? _logger;

    [ObservableProperty] private string _symbol = "BTC-USDT";
    [ObservableProperty] private string _timeframe = "1h";

    // Canvas geometry — kept square so the radar isn't elongated.
    [ObservableProperty] private double _canvasSize = 360;
    private double Center => CanvasSize / 2.0;
    private double Radius => (CanvasSize / 2.0) - 38; // padding for labels

    // Background reference geometry (4 concentric hexagons + 6 axis spokes).
    [ObservableProperty] private ObservableCollection<RadarRing> _radarRings = new();
    [ObservableProperty] private ObservableCollection<RadarAxisSpoke> _radarSpokes = new();
    [ObservableProperty] private ObservableCollection<RadarAxisLabel> _radarLabels = new();

    // Value polygon — shows the actual scores.
    [ObservableProperty] private PointCollection _valuePolygon = new();
    [ObservableProperty] private ObservableCollection<RadarValueDot> _valueDots = new();

    // The 6 scores [0,1] — exposed for telemetry / external consumers.
    [ObservableProperty] private double _scoreMomentum;
    [ObservableProperty] private double _scoreTrendStrength;
    [ObservableProperty] private double _scoreVolumeRatio;
    [ObservableProperty] private double _scoreVolatility;
    [ObservableProperty] private double _scoreSentiment;
    [ObservableProperty] private double _scoreAlphaTrendAlignment;

    [ObservableProperty] private string _verdictLabel = "—";
    [ObservableProperty] private Brush _verdictColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private string _statusMessage = "Set a symbol and click Refresh to compute confidence.";
    [ObservableProperty] private bool _isRunning;

    public SignalConfidenceViewModel(
        IHistoricalMarketDataService? history = null,
        ILogger<SignalConfidenceViewModel>? logger = null)
    {
        _history = history;
        _logger = logger;
        BuildBackgroundGeometry();
        // Seed with a flat baseline so the chart shows something on first render.
        ApplyScores(0.20, 0.20, 0.20, 0.20, 0.20, 0.20);
    }

    partial void OnCanvasSizeChanged(double value) => BuildBackgroundGeometry();

    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_history is null)
        {
            StatusMessage = "Historical data service not wired in DI — radar runs on stub data.";
            ApplySyntheticScores();
            return;
        }

        if (IsRunning) return;
        IsRunning = true;
        StatusMessage = $"Fetching {Symbol} @ {Timeframe}...";
        try
        {
            var end = System.DateTime.UtcNow;
            var start = end.AddDays(-30);
            var candles = await _history.GetHistoricalCandlesAsync(Symbol, Timeframe, start, end)
                .ConfigureAwait(true);
            if (candles is null || candles.Count < 30)
            {
                StatusMessage = $"Not enough candles for {Symbol} ({candles?.Count ?? 0}). Showing synthetic baseline.";
                ApplySyntheticScores();
                return;
            }
            var scores = ComputeScoresFromCandles(candles);
            ApplyScores(scores.momentum, scores.trend, scores.volume, scores.volatility, scores.sentiment, scores.alphaAlign);
            StatusMessage = $"{Symbol} @ {Timeframe} · {candles.Count} bars · all six dimensions normalized to [0,1].";
        }
        catch (System.Exception ex)
        {
            _logger?.LogError(ex, "Radar refresh failed for {Symbol}", Symbol);
            StatusMessage = $"Failed: {ex.Message}";
            ApplySyntheticScores();
        }
        finally { IsRunning = false; }
    }

    /// <summary>Public setter so external code (live signal events) can push
    /// the latest 6 scores in without going through the candle fetch path.</summary>
    public void SetScores(double momentum, double trend, double volume,
                          double volatility, double sentiment, double alphaAlign)
        => ApplyScores(momentum, trend, volume, volatility, sentiment, alphaAlign);

    private void ApplySyntheticScores()
    {
        // Deterministic mid-strength signal for demo purposes.
        ApplyScores(0.55, 0.62, 0.48, 0.40, 0.50, 0.58);
    }

    private void ApplyScores(double momentum, double trend, double volume,
                             double volatility, double sentiment, double alphaAlign)
    {
        ScoreMomentum = System.Math.Clamp(momentum, 0, 1);
        ScoreTrendStrength = System.Math.Clamp(trend, 0, 1);
        ScoreVolumeRatio = System.Math.Clamp(volume, 0, 1);
        ScoreVolatility = System.Math.Clamp(volatility, 0, 1);
        ScoreSentiment = System.Math.Clamp(sentiment, 0, 1);
        ScoreAlphaTrendAlignment = System.Math.Clamp(alphaAlign, 0, 1);
        BuildValueGeometry();
        UpdateVerdict();
    }

    private void UpdateVerdict()
    {
        var avg = (ScoreMomentum + ScoreTrendStrength + ScoreVolumeRatio +
                   ScoreVolatility + ScoreSentiment + ScoreAlphaTrendAlignment) / 6.0;
        if (avg >= 0.75)
        {
            VerdictLabel = $"ALL-IN  ·  {avg:P0}";
            VerdictColor = new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B));
        }
        else if (avg >= 0.50)
        {
            VerdictLabel = $"STRONG  ·  {avg:P0}";
            VerdictColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
        }
        else if (avg >= 0.30)
        {
            VerdictLabel = $"WEAK  ·  {avg:P0}";
            VerdictColor = new SolidColorBrush(Color.FromRgb(0xC9, 0xA1, 0x3B));
        }
        else
        {
            VerdictLabel = $"AVOID  ·  {avg:P0}";
            VerdictColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
        }
    }

    private static readonly string[] AxisNames =
    {
        "Momentum", "Trend", "Volume", "Volatility", "Sentiment", "AlphaTrend"
    };

    /// <summary>Rebuilds the static background geometry: 4 reference rings at
    /// 25/50/75/100% and 6 spokes from center to outer ring with labels.</summary>
    private void BuildBackgroundGeometry()
    {
        RadarRings.Clear();
        RadarSpokes.Clear();
        RadarLabels.Clear();

        for (int r = 1; r <= 4; r++)
        {
            double scale = r / 4.0;
            var pts = new PointCollection(7);
            for (int i = 0; i <= 6; i++)
            {
                int axis = i % 6;
                pts.Add(AxisPoint(axis, scale));
            }
            RadarRings.Add(new RadarRing
            {
                Points = pts,
                IsOuter = r == 4
            });
        }

        for (int i = 0; i < 6; i++)
        {
            var inner = new Point(Center, Center);
            var outer = AxisPoint(i, 1.0);
            RadarSpokes.Add(new RadarAxisSpoke
            {
                Points = new PointCollection { inner, outer }
            });
            // Label sits just outside the outer ring on each axis.
            var labelPoint = AxisPoint(i, 1.18);
            RadarLabels.Add(new RadarAxisLabel
            {
                Text = AxisNames[i],
                X = labelPoint.X - 30,
                Y = labelPoint.Y - 8
            });
        }
    }

    private void BuildValueGeometry()
    {
        var pts = new PointCollection(7);
        var dots = new ObservableCollection<RadarValueDot>();
        double[] scores =
        {
            ScoreMomentum, ScoreTrendStrength, ScoreVolumeRatio,
            ScoreVolatility, ScoreSentiment, ScoreAlphaTrendAlignment
        };
        for (int i = 0; i <= 6; i++)
        {
            int axis = i % 6;
            var p = AxisPoint(axis, scores[axis]);
            pts.Add(p);
            if (i < 6) dots.Add(new RadarValueDot { X = p.X - 4, Y = p.Y - 4 });
        }
        ValuePolygon = pts;
        ValueDots = dots;
    }

    /// <summary>Computes the cartesian point on axis i (0..5) at radial scale [0..1].
    /// Axis 0 is straight up; rotation goes clockwise.</summary>
    private Point AxisPoint(int axis, double scale)
    {
        // Start at top (-π/2), step 2π/6.
        double angle = -System.Math.PI / 2 + axis * (System.Math.PI * 2 / 6);
        double r = Radius * scale;
        return new Point(Center + System.Math.Cos(angle) * r,
                         Center + System.Math.Sin(angle) * r);
    }

    /// <summary>Lightweight TA derivation of the 6 confidence scores.
    /// Each metric is rolled into [0,1] using closed-form rules tuned for
    /// crypto / 1h candles (good enough for a visual cue; not a trading rule).</summary>
    private static (double momentum, double trend, double volume, double volatility, double sentiment, double alphaAlign)
        ComputeScoresFromCandles(System.Collections.Generic.IReadOnlyList<Candle> candles)
    {
        int n = candles.Count;
        double[] closes = new double[n];
        for (int i = 0; i < n; i++) closes[i] = (double)candles[i].Close;

        // Momentum: 14-bar ROC, normalized to ±5% → [0,1].
        int look = System.Math.Min(14, n - 1);
        double roc = look > 0 ? (closes[n - 1] - closes[n - 1 - look]) / closes[n - 1 - look] : 0.0;
        double momentum = System.Math.Clamp(0.5 + roc / 0.10, 0, 1);

        // Trend strength: |slope| of last 20 closes / mean → bigger = stronger.
        int trendLook = System.Math.Min(20, n);
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < trendLook; i++)
        {
            sumX += i; sumY += closes[n - trendLook + i];
            sumXY += i * closes[n - trendLook + i];
            sumX2 += i * i;
        }
        double slope = (trendLook * sumXY - sumX * sumY) / System.Math.Max(1e-9, (trendLook * sumX2 - sumX * sumX));
        double meanClose = sumY / trendLook;
        double trendNorm = meanClose > 0 ? System.Math.Abs(slope) / (meanClose * 0.001) : 0;
        double trend = System.Math.Clamp(trendNorm / 5.0, 0, 1);

        // Volume ratio: last bar volume / 20-bar avg, capped at 3× → [0,1].
        double volAvg = 0; int volLook = System.Math.Min(20, n);
        for (int i = n - volLook; i < n; i++) volAvg += (double)candles[i].Volume;
        volAvg /= volLook;
        double volRatio = volAvg > 0 ? (double)candles[n - 1].Volume / volAvg : 0;
        double volume = System.Math.Clamp(volRatio / 3.0, 0, 1);

        // Volatility: stdev of returns / 5% → [0,1] (high vol = high score).
        var rets = new System.Collections.Generic.List<double>(n - 1);
        for (int i = 1; i < n; i++) if (closes[i - 1] > 0) rets.Add(System.Math.Log(closes[i] / closes[i - 1]));
        double mean = 0; foreach (var r in rets) mean += r; mean /= System.Math.Max(1, rets.Count);
        double variance = 0; foreach (var r in rets) variance += (r - mean) * (r - mean);
        variance /= System.Math.Max(1, rets.Count - 1);
        double stdev = System.Math.Sqrt(variance);
        double volatility = System.Math.Clamp(stdev / 0.02, 0, 1);

        // Sentiment proxy: bullish-candle ratio in last 20 bars.
        int bullCount = 0;
        for (int i = System.Math.Max(0, n - 20); i < n; i++) if (candles[i].IsBullish) bullCount++;
        double sentiment = (double)bullCount / System.Math.Min(20, n);

        // AlphaTrend alignment proxy: (close vs 20-EMA) bias × momentum sign agreement.
        double ema = closes[0]; double alpha = 2.0 / 21.0;
        for (int i = 1; i < n; i++) ema = alpha * closes[i] + (1 - alpha) * ema;
        double bias = closes[n - 1] > ema ? 1 : -1;
        double mSign = roc >= 0 ? 1 : -1;
        double alphaAlign = bias == mSign ? System.Math.Clamp(0.5 + System.Math.Abs(roc) / 0.05, 0.5, 1)
                                          : System.Math.Clamp(0.5 - System.Math.Abs(roc) / 0.05, 0, 0.5);

        return (momentum, trend, volume, volatility, sentiment, alphaAlign);
    }
}

public partial class RadarRing : ObservableObject
{
    [ObservableProperty] private PointCollection _points = new();
    [ObservableProperty] private bool _isOuter;
}

public partial class RadarAxisSpoke : ObservableObject
{
    [ObservableProperty] private PointCollection _points = new();
}

public partial class RadarAxisLabel : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
}

public partial class RadarValueDot : ObservableObject
{
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
}
