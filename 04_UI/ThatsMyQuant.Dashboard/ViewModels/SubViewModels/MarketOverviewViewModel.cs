using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows.Media;
using System;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public partial class MarketOverviewViewModel : ObservableObject
{
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));

    [ObservableProperty] private double _chartCanvasWidth = 800;
    [ObservableProperty] private double _chartCanvasHeight = 400;
    
    public List<Candle> ChartSourceCandles { get; } = new();

    // --- Top Index Tickers ---
    [ObservableProperty] private string _xu100Price = "--";
    [ObservableProperty] private string _xu100Change = "--";
    [ObservableProperty] private Brush _xu100Color = GrayBrush;

    [ObservableProperty] private string _btcPrice = "--";
    [ObservableProperty] private string _btcChange = "--";
    [ObservableProperty] private Brush _btcColor = GrayBrush;

    [ObservableProperty] private string _usdTryPrice = "--";
    [ObservableProperty] private string _usdTryChange = "--";
    [ObservableProperty] private Brush _usdTryColor = GrayBrush;

    [ObservableProperty] private string _xautryPrice = "--";
    [ObservableProperty] private string _xautryChange = "--";
    [ObservableProperty] private Brush _xautryColor = GrayBrush;

    // --- Market & Symbol Selector ---
    public ObservableCollection<string> Markets { get; } = new() { "BIST", "Crypto", "Forex" };
    [ObservableProperty] private string _selectedMarket = "BIST";
    public ObservableCollection<string> Symbols { get; } = new();
    [ObservableProperty] private string _selectedSymbol = "THYAO";

    // --- Timeframe Selection (for selector) ---
    public ObservableCollection<string> TimeframeOptions { get; } = new() { "1m", "5m", "15m", "1h", "4h", "1d" };
    [ObservableProperty] private string _selectedTimeframe = "1h";


    [ObservableProperty] private string _chartHeaderTitle = "Chart";
    [ObservableProperty] private string _chartHeaderOhlc = "O -- H -- L -- C --";
    [ObservableProperty] private Brush _chartHeaderChangeColor = GrayBrush;
    [ObservableProperty] private string _currentPriceMarker = "--";
    [ObservableProperty] private double _currentPriceMarkerY = 150;
    [ObservableProperty] private string _marketPulse = "Awaiting fresh market scan.";
    [ObservableProperty] private string _botStatusSummary = "The bot is waiting for the next qualified HTF setup.";
    [ObservableProperty] private string _dailyMarketBriefing = "Daily market briefing will appear after the next analysis cycle.";

    // --- Chart Focus / Inspector ---
    [ObservableProperty] private string _chartFocusTitle = "Chart Inspector";
    [ObservableProperty] private string _chartFocusSummary = "Hover over the chart to inspect candles.";
    [ObservableProperty] private string _chartFocusPrice = "Close: --";
    [ObservableProperty] private string _chartFocusRange = "Range: --";
    [ObservableProperty] private string _chartFocusVolume = "Volume: --";
    [ObservableProperty] private string _chartFocusChange = "Change: --";
    [ObservableProperty] private string _executionServiceStatus = "Execution service pending";
    [ObservableProperty] private string _aiProviderStatus = "AI provider pending";
    [ObservableProperty] private string _runtimeIntegrityStatus = "Runtime integrity pending";
    [ObservableProperty] private Brush _runtimeIntegrityColor = GrayBrush;
    [ObservableProperty] private string _configValidationStatus = "Startup validation pending";
    [ObservableProperty] private string _configValidationSummary = "Runtime configuration will be validated at startup.";

    // --- Charting Properties ---
    public ObservableCollection<CandleViewModel> ChartCandles { get; } = new();
    public ObservableCollection<ChartGridLineViewModel> HorizontalGridLines { get; } = new();
    public ObservableCollection<ChartGridLineViewModel> VerticalGridLines { get; } = new();
    public ObservableCollection<ChartAxisLabel> PriceScaleLabels { get; } = new();
    public ObservableCollection<ChartAxisLabel> TimeScaleLabels { get; } = new();
    public ObservableCollection<ReasoningOverlayViewModel> ReasoningOverlays { get; } = new();
    public ObservableCollection<BacktestMarker> BacktestMarkers { get; } = new();

    [ObservableProperty] private PointCollection _chartPoints = new();
    [ObservableProperty] private string _currentDate = DateTime.Now.ToString("dd MMMM yyyy");
    [ObservableProperty] private string _visualReasoningSummary = "Visual reasoning overlays will appear after analysis.";

    private static readonly Brush BuyColorBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xBB));
    private static readonly Brush SellColorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x66));

    public void InspectChartAt(double logicalX)
    {
        if (ChartSourceCandles.Count == 0 || ChartCanvasWidth <= 0)
        {
            ClearChartInspection();
            return;
        }

        var clampedX = Math.Clamp(logicalX, 0, ChartCanvasWidth);
        var index = Math.Clamp(
            (int)Math.Round(clampedX / ChartCanvasWidth * (ChartSourceCandles.Count - 1)),
            0,
            ChartSourceCandles.Count - 1);
        
        var candle = ChartSourceCandles[index];
        var previousClose = index > 0 ? ChartSourceCandles[index - 1].Close : candle.Open;
        var percentChange = previousClose == 0 ? 0 : (candle.Close - previousClose) / previousClose * 100;

        ChartFocusTitle = $"{SelectedSymbol} {candle.Timestamp:dd MMM HH:mm}";
        ChartFocusSummary = $"Open {candle.Open:F2} | High {candle.High:F2} | Low {candle.Low:F2}";
        ChartFocusPrice = $"Close: {candle.Close:F2}";
        ChartFocusRange = $"Range: {(candle.High - candle.Low):F2}";
        ChartFocusVolume = $"Volume: {candle.Volume:N0}";
        ChartFocusChange = $"Change: {percentChange:+0.00;-0.00;0.00}%";
        
        SetChartHeader(candle, previousClose);
    }

    public void ClearChartInspection()
    {
        ChartFocusTitle = "Chart Inspector";
        ChartFocusSummary = "Hover over the chart to inspect candles.";
        ChartFocusPrice = "Close: --";
        ChartFocusRange = "Range: --";
        ChartFocusVolume = "Volume: --";
        ChartFocusChange = "Change: --";
    }

    public void SetChartHeader(Candle candle, decimal previousClose)
    {
        var change = previousClose == 0 ? 0 : (candle.Close - previousClose) / previousClose * 100;
        ChartHeaderOhlc = $"O {candle.Open:F2}  H {candle.High:F2}  L {candle.Low:F2}  C {candle.Close:F2}  {change:+0.00;-0.00;0.00}%";
        ChartHeaderChangeColor = change >= 0 ? GreenBrush : RedBrush;
    }

    public void NormalizeChart(List<Candle> candles)
    {
        InvokeOnUi(() => 
        {
            ChartCandles.Clear();
            PriceScaleLabels.Clear();
            TimeScaleLabels.Clear();
            HorizontalGridLines.Clear();
            VerticalGridLines.Clear();
        });

        ChartSourceCandles.Clear();
        ChartSourceCandles.AddRange(candles);

        if (candles.Count == 0)
        {
            ChartHeaderTitle = $"{SelectedSymbol} · {SelectedTimeframe} · {SelectedMarket}";
            ChartHeaderOhlc = "O -- H -- L -- C --";
            CurrentPriceMarker = "--";
            return;
        }

        var (min, max) = GetDisplayRange(candles);
        decimal range = Math.Max(max - min, 0.00000001m);

        double canvasWidth = ChartCanvasWidth;
        double canvasHeight = ChartCanvasHeight;
        double candleWidth = candles.Count > 1 ? (canvasWidth / candles.Count) * 0.82 : 40;

        for (int i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            double x = candles.Count > 1 ? (double)i / (candles.Count - 1) * (canvasWidth - candleWidth) : 0;

            var open = Clamp(candle.Open, min, max);
            var close = Clamp(candle.Close, min, max);
            var high = Clamp(candle.High, min, max);
            var low = Clamp(candle.Low, min, max);

            double openY = canvasHeight - (double)((open - min) / range) * canvasHeight;
            double closeY = canvasHeight - (double)((close - min) / range) * canvasHeight;
            double highY = canvasHeight - (double)((high - min) / range) * canvasHeight;
            double lowY = canvasHeight - (double)((low - min) / range) * canvasHeight;

            bool isBullish = candle.Close >= candle.Open;
            var color = isBullish ? BuyColorBrush : SellColorBrush;

            InvokeOnUi(() => 
            {
                ChartCandles.Add(new CandleViewModel
                {
                    X = x,
                    WickX = x + (candleWidth / 2),
                    BodyY = Math.Min(openY, closeY),
                    BodyHeight = Math.Max(Math.Abs(openY - closeY), 1.2),
                    WickY1 = highY,
                    WickY2 = lowY,
                    Width = candleWidth,
                    Color = color
                });
            });
        }

        InvokeOnUi(() => PopulateChartScales(candles, min, max, canvasWidth, canvasHeight));

        var points = new PointCollection();
        for (int i = 0; i < candles.Count; i++)
        {
            double x = (double)i / Math.Max(1, candles.Count - 1) * canvasWidth;
            double y = canvasHeight - (double)((Clamp(candles[i].Close, min, max) - min) / range) * canvasHeight;
            points.Add(new System.Windows.Point(x, y));
        }
        ChartPoints = points;

        var last = candles[^1];
        ChartHeaderTitle = $"{SelectedSymbol} · {SelectedTimeframe} · {SelectedMarket}";
        SetChartHeader(last, candles.Count > 1 ? candles[^2].Close : last.Open);
        CurrentPriceMarker = $"{last.Close:F2}";
        CurrentPriceMarkerY = canvasHeight - (double)((Clamp(last.Close, min, max) - min) / range) * canvasHeight;
    }

    private void PopulateChartScales(List<Candle> candles, decimal min, decimal max, double canvasWidth, double canvasHeight)
    {
        const int horizontalSteps = 5;
        const int verticalSteps = 6;

        for (int i = 0; i <= horizontalSteps; i++)
        {
            var y = canvasHeight / horizontalSteps * i;
            var price = max - (max - min) * (decimal)i / horizontalSteps;
            HorizontalGridLines.Add(new ChartGridLineViewModel { X1 = 0, Y1 = y, X2 = canvasWidth, Y2 = y });
            PriceScaleLabels.Add(new ChartAxisLabel
            {
                Text = price.ToString("F2"),
                X = canvasWidth + 10,
                Y = y - 8
            });
        }

        for (int i = 0; i < verticalSteps; i++)
        {
            var x = canvasWidth / Math.Max(1, verticalSteps - 1) * i;
            var index = Math.Min(candles.Count - 1, (int)Math.Round((double)i / Math.Max(1, verticalSteps - 1) * (candles.Count - 1)));
            VerticalGridLines.Add(new ChartGridLineViewModel { X1 = x, Y1 = 0, X2 = x, Y2 = canvasHeight });
            TimeScaleLabels.Add(new ChartAxisLabel
            {
                Text = candles[index].Timestamp.ToString(candles.Count > 120 ? "dd MMM" : "dd MMM HH:mm"),
                X = Math.Max(0, x - 24),
                Y = canvasHeight + 10
            });
        }
    }

    private static (decimal Min, decimal Max) GetDisplayRange(List<Candle> candles)
    {
        if (candles.Count == 0) return (0m, 1m);
        var lows = candles.Select(c => c.Low).OrderBy(v => v).ToList();
        var highs = candles.Select(c => c.High).OrderBy(v => v).ToList();
        var trim = candles.Count >= 20 ? Math.Max(1, candles.Count / 20) : 0;
        var min = lows[Math.Min(trim, lows.Count - 1)];
        var max = highs[Math.Max(0, highs.Count - 1 - trim)];
        if (max <= min) { min = candles.Min(c => c.Low); max = candles.Max(c => c.High); }
        var padding = Math.Max((max - min) * 0.08m, 0.01m);
        min -= padding; max += padding;
        if (max <= min) max = min + 1m;
        return (min, max);
    }

    private static decimal Clamp(decimal value, decimal min, decimal max) => Math.Clamp(value, min, max);

    private void InvokeOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(action);
        else action();
    }
}

