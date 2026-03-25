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

    [ObservableProperty] private bool _isTradingViewActive = false;

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
}
