using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using ThatsMyQuant.Dashboard.ViewModels;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

namespace ThatsMyQuant.Dashboard.Controls;

public partial class ScientificChartControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _chartHtml;
    private MarketOverviewViewModel? _viewModel;
    private MainViewModel? _hostViewModel;
    private bool _webViewReady;
    private bool _pendingRefresh;

    public ScientificChartControl()
    {
        InitializeComponent();
        _chartHtml = LoadChartHtml();
        DataContextChanged += ScientificChartControl_DataContextChanged;
        Loaded += ScientificChartControl_Loaded;
    }

    private async void ScientificChartControl_Loaded(object sender, RoutedEventArgs e)
    {
        AttachHostViewModel();

        if (_webViewReady)
        {
            return;
        }

        try
        {
            await ChartWebView.EnsureCoreWebView2Async();
            ChartWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            ChartWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ChartWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            ChartWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ChartWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            ChartWebView.NavigateToString(_chartHtml);
        }
        catch
        {
            // The dashboard can still function without the embedded chart engine.
        }
    }

    private void ScientificChartControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromViewModel();

        _viewModel = DataContext as MarketOverviewViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ReasoningOverlays.CollectionChanged += ObservableCollection_CollectionChanged;
        _viewModel.BacktestMarkers.CollectionChanged += ObservableCollection_CollectionChanged;
        _viewModel.PriceScaleLabels.CollectionChanged += ObservableCollection_CollectionChanged;
        _viewModel.TimeScaleLabels.CollectionChanged += ObservableCollection_CollectionChanged;
        ScheduleChartRefresh();
    }

    private void AttachHostViewModel()
    {
        var hostViewModel = Window.GetWindow(this)?.DataContext as MainViewModel;
        if (ReferenceEquals(hostViewModel, _hostViewModel))
        {
            return;
        }

        if (_hostViewModel is not null)
        {
            _hostViewModel.PropertyChanged -= HostViewModel_PropertyChanged;
        }

        _hostViewModel = hostViewModel;
        if (_hostViewModel is not null)
        {
            _hostViewModel.PropertyChanged += HostViewModel_PropertyChanged;
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.ReasoningOverlays.CollectionChanged -= ObservableCollection_CollectionChanged;
        _viewModel.BacktestMarkers.CollectionChanged -= ObservableCollection_CollectionChanged;
        _viewModel.PriceScaleLabels.CollectionChanged -= ObservableCollection_CollectionChanged;
        _viewModel.TimeScaleLabels.CollectionChanged -= ObservableCollection_CollectionChanged;
    }

    private void ObservableCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScheduleChartRefresh();

    private void HostViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentPrice)
            or nameof(MainViewModel.StopLoss)
            or nameof(MainViewModel.TakeProfit)
            or nameof(MainViewModel.NearestSupport)
            or nameof(MainViewModel.NearestResistance)
            or nameof(MainViewModel.BBUpper)
            or nameof(MainViewModel.BBLower)
            or nameof(MainViewModel.RSI)
            or nameof(MainViewModel.MACDStatus)
            or nameof(MainViewModel.EMAStatus))
        {
            ScheduleChartRefresh();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MarketOverviewViewModel.ChartCanvasWidth)
            or nameof(MarketOverviewViewModel.ChartCanvasHeight)
            or nameof(MarketOverviewViewModel.SelectedSymbol)
            or nameof(MarketOverviewViewModel.SelectedMarket)
            or nameof(MarketOverviewViewModel.SelectedTimeframe)
            or nameof(MarketOverviewViewModel.CurrentPriceMarker))
        {
            ScheduleChartRefresh();
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        _viewModel.ChartCanvasWidth = Math.Max(320, e.NewSize.Width - 170);
        _viewModel.ChartCanvasHeight = Math.Max(260, e.NewSize.Height - 90);
        ScheduleChartRefresh();
    }

    private async void ScheduleChartRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(ScheduleChartRefresh);
            return;
        }

        _pendingRefresh = true;
        if (!_webViewReady || _viewModel is null)
        {
            return;
        }

        _pendingRefresh = false;
        await DispatchChartStateAsync();
    }

    private async Task DispatchChartStateAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(async () => await DispatchChartStateAsync());
            return;
        }

        if (!_webViewReady || _viewModel is null || ChartWebView.CoreWebView2 is null)
        {
            return;
        }

        var width = Math.Max(1, _viewModel.ChartCanvasWidth);
        var height = Math.Max(1, _viewModel.ChartCanvasHeight);

        var payload = new
        {
            type = "state",
            theme = new
            {
                background = "#0B0E13",
                panel = "#0F131B",
                grid = "#171D26",
                text = "#D6E2F0",
                muted = "#7A8898",
                bullish = "#00FFBB",
                bearish = "#FF3366",
                accent = "#10B7FF"
            },
            meta = new
            {
                symbol = _viewModel.SelectedSymbol,
                market = _viewModel.SelectedMarket,
                timeframe = _viewModel.SelectedTimeframe,
                currentPrice = _viewModel.CurrentPriceMarker,
                width,
                height
            },
            candles = _viewModel.ChartSourceCandles.Select(c => new
            {
                time = c.Timestamp,
                open = c.Open,
                high = c.High,
                low = c.Low,
                close = c.Close,
                volume = c.Volume
            }),
            overlays = _viewModel.ReasoningOverlays.Select(o => new
            {
                x = SafeRatio(o.X, width),
                y = SafeRatio(o.Y, height),
                width = SafeRatio(o.Width, width),
                height = SafeRatio(o.Height, height),
                title = o.Title,
                detail = o.Detail,
                fill = ConvertBrushToCss(o.Fill),
                stroke = ConvertBrushToCss(o.Stroke),
                textColor = ConvertBrushToCss(o.TextColor)
            }),
            backtestMarkers = _viewModel.BacktestMarkers.Select(m => new
            {
                x = SafeRatio(m.X, width),
                y = SafeRatio(m.Y, height),
                color = ConvertBrushToCss(m.Color),
                direction = m.Direction,
                toolTip = m.ToolTip
            }),
            orderLines = BuildOrderLines(),
            executionMarkers = BuildExecutionMarkers()
        };

        ChartWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
        await Task.CompletedTask;
    }

    private void ChartWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _webViewReady = e.IsSuccess;
        if (_webViewReady && _pendingRefresh)
        {
            ScheduleChartRefresh();
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var messageType = typeElement.GetString();
            if (string.Equals(messageType, "hover", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("index", out var indexElement))
            {
                var count = _viewModel.ChartSourceCandles.Count;
                if (count > 0)
                {
                    var index = Math.Clamp(indexElement.GetInt32(), 0, count - 1);
                    var logicalX = count == 1
                        ? 0
                        : index / (double)(count - 1) * _viewModel.ChartCanvasWidth;
                    _viewModel.InspectChartAt(logicalX);
                }
            }
            else if (string.Equals(messageType, "leave", StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.ClearChartInspection();
            }
        }
        catch
        {
        }
    }

    private async Task SendChartCommandAsync(object command)
    {
        if (!_webViewReady || ChartWebView.CoreWebView2 is null)
        {
            return;
        }

        ChartWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(command, JsonOptions));
        await Task.CompletedTask;
    }

    private async void PanTool_Click(object sender, RoutedEventArgs e)
        => await SendChartCommandAsync(new { type = "tool", tool = "pan" });

    private async void CrosshairTool_Click(object sender, RoutedEventArgs e)
        => await SendChartCommandAsync(new { type = "tool", tool = "crosshair" });

    private async void TrendTool_Click(object sender, RoutedEventArgs e)
        => await SendChartCommandAsync(new { type = "tool", tool = "trendline" });

    private async void HorizontalLineTool_Click(object sender, RoutedEventArgs e)
        => await SendChartCommandAsync(new { type = "tool", tool = "horizontal" });

    private async void VerticalLineTool_Click(object sender, RoutedEventArgs e)
        => await SendChartCommandAsync(new { type = "tool", tool = "vertical" });

    private async void FibTool_Click(object sender, RoutedEventArgs e)
        => await SendChartCommandAsync(new { type = "tool", tool = "fib" });

    private async void RectangleTool_Click(object sender, RoutedEventArgs e)
        => await SendChartCommandAsync(new { type = "tool", tool = "rectangle" });

    private async void UndoTool_Click(object sender, RoutedEventArgs e)
        => await SendChartCommandAsync(new { type = "undo" });

    private async void ResetView_Click(object sender, RoutedEventArgs e)
        => await SendChartCommandAsync(new { type = "reset" });

    private IEnumerable<object> BuildOrderLines()
    {
        if (_hostViewModel is null)
        {
            return [];
        }

        var lines = new List<object>();
        AddLineIfValid(lines, "Price", _hostViewModel.CurrentPrice, "rgba(16,183,255,0.95)", false);
        AddLineIfValid(lines, "Stop", _hostViewModel.StopLoss, "rgba(255,51,102,0.95)", true);
        AddLineIfValid(lines, "Target", _hostViewModel.TakeProfit, "rgba(0,255,187,0.95)", true);
        AddLineIfValid(lines, "Support", _hostViewModel.NearestSupport, "rgba(113,201,248,0.85)", true);
        AddLineIfValid(lines, "Resistance", _hostViewModel.NearestResistance, "rgba(255,180,92,0.85)", true);
        AddLineIfValid(lines, "BB Upper", (decimal)_hostViewModel.BBUpper, "rgba(180,180,255,0.72)", true);
        AddLineIfValid(lines, "BB Lower", (decimal)_hostViewModel.BBLower, "rgba(180,180,255,0.72)", true);
        return lines;
    }

    private IEnumerable<object> BuildExecutionMarkers()
    {
        if (_hostViewModel is null || _viewModel is null)
        {
            return [];
        }

        return _hostViewModel
            .GetSelectedSymbolTradesForChart(_viewModel.SelectedSymbol)
            .SelectMany(trade =>
            {
                var markers = new List<object>
                {
                    new
                    {
                        time = trade.EntryTime,
                        price = trade.EntryPrice,
                        side = trade.Side.ToString(),
                        markerType = "entry",
                        color = trade.Side == Contracts.Enums.TradeSide.Buy ? "rgba(0,255,187,0.95)" : "rgba(255,51,102,0.95)",
                        label = $"{trade.Side} entry"
                    }
                };

                if (trade.ExitTime.HasValue && trade.ExitPrice > 0m)
                {
                    markers.Add(new
                    {
                        time = trade.ExitTime.Value,
                        price = trade.ExitPrice,
                        side = trade.Side.ToString(),
                        markerType = "exit",
                        color = trade.RealisedPnL >= 0 ? "rgba(88,214,141,0.95)" : "rgba(149,165,166,0.95)",
                        label = $"{trade.Side} exit"
                    });
                }

                return markers;
            })
            .ToList();
    }

    private static void AddLineIfValid(ICollection<object> lines, string label, decimal value, string color, bool dashed)
    {
        if (value <= 0m)
        {
            return;
        }

        lines.Add(new
        {
            label,
            value,
            color,
            dashed
        });
    }

    private static double SafeRatio(double value, double total)
        => total <= 0 ? 0 : Math.Clamp(value / total, 0, 1);

    private static string ConvertBrushToCss(System.Windows.Media.Brush brush)
    {
        if (brush is not System.Windows.Media.SolidColorBrush solid)
        {
            return "#FFFFFF";
        }

        var color = solid.Color;
        return $"rgba({color.R}, {color.G}, {color.B}, {color.A / 255d:0.###})";
    }

    private static string LoadChartHtml()
    {
        var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal-chart.html");
        return File.Exists(assetPath)
            ? File.ReadAllText(assetPath)
            : "<html><body style='background:#0B0E13;color:white;font-family:Segoe UI'>Chart asset missing.</body></html>";
    }
}
