using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ThatsMyQuant.Dashboard.ViewModels;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

namespace ThatsMyQuant.Dashboard.Controls;

public partial class ScientificChartControl : UserControl
{
    private MarketOverviewViewModel? _viewModel;
    private bool _initialized;
    private bool _isPanning;
    private Point _lastMousePosition;

    public ScientificChartControl()
    {
        InitializeComponent();
        Loaded += ScientificChartControl_Loaded;
        DataContextChanged += ScientificChartControl_DataContextChanged;
    }

    private async void ScientificChartControl_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureBrowserAsync();
        await RenderChartAsync();
    }

    private async void ScientificChartControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        _viewModel = DataContext as MarketOverviewViewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        await RenderChartAsync();
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MarketOverviewViewModel.SelectedSymbol)
            or nameof(MarketOverviewViewModel.SelectedMarket)
            or nameof(MarketOverviewViewModel.SelectedTimeframe))
        {
            await Dispatcher.InvokeAsync(async () => await RenderChartAsync());
        }
    }

    private async Task EnsureBrowserAsync()
    {
        if (_initialized)
        {
            return;
        }

        await TradingViewBrowser.EnsureCoreWebView2Async();
        if (TradingViewBrowser.CoreWebView2 != null)
        {
            TradingViewBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            TradingViewBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            TradingViewBrowser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            TradingViewBrowser.CoreWebView2.Settings.IsZoomControlEnabled = false;
            TradingViewBrowser.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            TradingViewBrowser.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
        }

        _initialized = true;
    }

    private async Task RenderChartAsync()
    {
        if (_viewModel == null)
        {
            return;
        }

        await EnsureBrowserAsync();

        // Lock Browser Scale to prevent clippings/scaling issues
        TradingViewBrowser.ZoomFactor = 1.0;

        var symbol = ResolveTradingViewSymbol(_viewModel.SelectedMarket, _viewModel.SelectedSymbol);
        var interval = ResolveTradingViewInterval(_viewModel.SelectedTimeframe);
        var timezone = (_viewModel.SelectedMarket == "BIST") ? "Europe/Istanbul" : "Etc/UTC";
        
        // Simplified and more robust payload
        var payload = JsonSerializer.Serialize(new
        {
            autosize = true,
            symbol = symbol,
            interval = interval,
            timezone = timezone,
            theme = "dark",
            style = "1",
            locale = "en",
            enable_publishing = false,
            hide_side_toolbar = false,              // SHOW DRAWING TOOLS
            allow_symbol_change = true,             // SHOW SYMBOL SEARCH
            hide_top_toolbar = false,               // SHOW TIMEFRAME/TYPE SELECTOR
            save_image = false,
            container_id = "tv_chart_container",
            studies = new[] { "Volume@tv-basicstudies" }
        });

        var html = $$"""
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8" />
  <style>
    html, body {
      margin: 0;
      padding: 0;
      width: 100vw;
      height: 100vh;
      overflow: hidden;
      background: #0B0E13;
      font-family: -apple-system, BlinkMacSystemFont, 'Trebuchet MS', Roboto, Ubuntu, sans-serif;
    }
    #tv_chart_container {
      width: 100%;
      height: 100%;
    }
  </style>
</head>
<body>
  <div id="tv_chart_container"></div>
  <script type="text/javascript" src="https://s3.tradingview.com/tv.js"></script>
  <script type="text/javascript">
    try {
        new TradingView.widget({{payload}});
    } catch(e) {
        console.error(e);
    }
    
    // Notify WPF that we are initialized - IMMEDIATE & FAIL-SAFE
    window.chrome.webview.postMessage({ type: 'tv-ready' });
    setTimeout(() => { window.chrome.webview.postMessage({ type: 'tv-ready' }); }, 1500);
  </script>
</body>
</html>
""";

        TradingViewBrowser.NavigateToString(html);
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = JsonNode.Parse(e.WebMessageAsJson)?["type"]?.GetValue<string>();
            if (message == "tv-ready")
            {
                // CRITICAL: Collapse the entire WPF Dashboard Border to let TV take the full container
                TradingViewBrowser.Visibility = Visibility.Visible;
                DashboardBorder.Visibility = Visibility.Collapsed;

                if (DataContext is MarketOverviewViewModel movm)
                {
                    movm.IsTradingViewActive = true;
                }
                return;
            }

            if (message == "tv-error")
            {
                TradingViewBrowser.Visibility = Visibility.Collapsed;
                DashboardBorder.Visibility = Visibility.Visible;
                
                // Also ensure WPF sub-elements are restored
                WpfHeaderLayer.Visibility = Visibility.Visible;
                WpfSidebarLayer.Visibility = Visibility.Visible;
                FallbackChartLayer.Visibility = Visibility.Visible;

                // Restore original rows/cols just in case
                MainControlGrid.RowDefinitions[0].Height = new GridLength(44);
                MainControlGrid.RowDefinitions[2].Height = new GridLength(34);
                MainControlGrid.ColumnDefinitions[0].Width = new GridLength(52);
                MainControlGrid.ColumnDefinitions[2].Width = new GridLength(82);
            }
        }
        catch
        {
            TradingViewBrowser.Visibility = Visibility.Collapsed;
            DashboardBorder.Visibility = Visibility.Visible;
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        _viewModel.ChartCanvasWidth = Math.Max(300, e.NewSize.Width - 140);
        _viewModel.ChartCanvasHeight = Math.Max(260, e.NewSize.Height - 90);
    }

    private static string ResolveTradingViewInterval(string timeframe)
    {
        return timeframe switch
        {
            "1m" => "1",
            "5m" => "5",
            "15m" => "15",
            "1h" => "60",
            "3h" => "180",
            "4h" => "240",
            "1d" => "D",
            _ => "60"
        };
    }

    private static string ResolveTradingViewSymbol(string market, string symbol)
    {
        return market switch
        {
            "BIST" => $"BIST:{symbol}",
            "Forex" => $"FX:{symbol.Replace("=X", string.Empty, StringComparison.OrdinalIgnoreCase)}",
            "Crypto" => ResolveCryptoSymbol(symbol),
            _ => symbol
        };
    }

    private static string ResolveCryptoSymbol(string symbol)
    {
        var compact = symbol.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("USD", string.Empty, StringComparison.OrdinalIgnoreCase);

        return compact.ToUpperInvariant() switch
        {
            "BTC" => "BINANCE:BTCUSDT",
            "ETH" => "BINANCE:ETHUSDT",
            "SOL" => "BINANCE:SOLUSDT",
            "XRP" => "BINANCE:XRPUSDT",
            "AVAX" => "BINANCE:AVAXUSDT",
            "LINK" => "BINANCE:LINKUSDT",
            _ => "BINANCE:BTCUSDT"
        };
    }

    private void ChartCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var st = ChartScaleTransform;
        var tt = ChartTranslateTransform;

        double zoom = e.Delta > 0 ? 1.2 : 0.8;

        if (st.ScaleX * zoom < 1) zoom = 1 / st.ScaleX;
        if (st.ScaleX * zoom > 50) zoom = 50 / st.ScaleX;

        Point mousePos = e.GetPosition(ChartCanvas);
        tt.X = (tt.X - mousePos.X) * zoom + mousePos.X;
        st.ScaleX *= zoom;

        ConstrainPan();
        e.Handled = true;
    }

    private void ChartCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _lastMousePosition = e.GetPosition(this);
        ChartCanvas.CaptureMouse();
    }

    private void ChartCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        ChartCanvas.ReleaseMouseCapture();
    }

    private void ChartCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var chartPosition = e.GetPosition(ChartCanvas);
        UpdateCrosshair(chartPosition);
        _viewModel?.InspectChartAt(chartPosition.X);

        if (!_isPanning) return;

        Point currentPosition = e.GetPosition(this);
        double offsetX = currentPosition.X - _lastMousePosition.X;

        var tt = ChartTranslateTransform;
        tt.X += offsetX;
        ConstrainPan();

        _lastMousePosition = currentPosition;
    }

    private void ChartCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            HideCrosshair();
            _viewModel?.ClearChartInspection();
        }
    }

    private void ChartCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ResetChart();
    }

    public void ResetChart()
    {
        ChartScaleTransform.ScaleX = 1;
        ChartScaleTransform.ScaleY = 1;
        ChartTranslateTransform.X = 0;
        ChartTranslateTransform.Y = 0;
        HideCrosshair();
        _viewModel?.ClearChartInspection();
    }

    private void ConstrainPan()
    {
        var tt = ChartTranslateTransform;
        var st = ChartScaleTransform;

        if (tt.X > 0) tt.X = 0;

        double logicalWidth = _viewModel?.ChartCanvasWidth ?? 800;
        double maxScrollLeft = logicalWidth - (logicalWidth * st.ScaleX);
        if (tt.X < maxScrollLeft) tt.X = maxScrollLeft;
    }

    private void UpdateCrosshair(Point chartPosition)
    {
        if (chartPosition.X < 0 || chartPosition.X > ChartCanvas.Width || chartPosition.Y < 0 || chartPosition.Y > ChartCanvas.Height)
        {
            HideCrosshair();
            return;
        }

        ShowLine(CrosshairVertical, chartPosition.X, 0, chartPosition.X, ChartCanvas.Height);
        ShowLine(CrosshairHorizontal, 0, chartPosition.Y, ChartCanvas.Width, chartPosition.Y);
    }

    private static void ShowLine(Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1;
        line.Y1 = y1;
        line.X2 = x2;
        line.Y2 = y2;
        line.Visibility = Visibility.Visible;
    }

    private void HideCrosshair()
    {
        CrosshairVertical.Visibility = Visibility.Collapsed;
        CrosshairHorizontal.Visibility = Visibility.Collapsed;
    }
}
