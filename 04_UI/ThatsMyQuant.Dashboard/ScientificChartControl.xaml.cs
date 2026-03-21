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

namespace ThatsMyQuant.Dashboard.Controls;

public partial class ScientificChartControl : UserControl
{
    private MainViewModel? _viewModel;
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

        _viewModel = DataContext as MainViewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        await RenderChartAsync();
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedSymbol)
            or nameof(MainViewModel.SelectedMarket)
            or nameof(MainViewModel.SelectedTimeframe))
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

        var symbol = ResolveTradingViewSymbol(_viewModel.SelectedMarket, _viewModel.SelectedSymbol);
        var interval = ResolveTradingViewInterval(_viewModel.SelectedTimeframe);
        var payload = JsonSerializer.Serialize(new
        {
            autosize = true,
            symbol,
            interval,
            timezone = "Europe/Istanbul",
            theme = "dark",
            style = "1",
            locale = "en",
            enable_publishing = false,
            allow_symbol_change = false,
            hide_top_toolbar = false,
            hide_legend = false,
            withdateranges = true,
            calendar = false,
            details = true,
            hotlist = false,
            studies = new[] { "Volume@tv-basicstudies" },
            container_id = "tv_chart_container",
            overrides = new Dictionary<string, string>
            {
                ["paneProperties.background"] = "#0B0E13",
                ["paneProperties.vertGridProperties.color"] = "#171D26",
                ["paneProperties.horzGridProperties.color"] = "#171D26",
                ["scalesProperties.textColor"] = "#7D8590",
                ["mainSeriesProperties.candleStyle.upColor"] = "#00FFBB",
                ["mainSeriesProperties.candleStyle.downColor"] = "#FF3366",
                ["mainSeriesProperties.candleStyle.borderUpColor"] = "#00FFBB",
                ["mainSeriesProperties.candleStyle.borderDownColor"] = "#FF3366",
                ["mainSeriesProperties.candleStyle.wickUpColor"] = "#00FFBB",
                ["mainSeriesProperties.candleStyle.wickDownColor"] = "#FF3366"
            }
        });

        var html = $$"""
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8" />
  <meta http-equiv="X-UA-Compatible" content="IE=edge" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style>
    html, body {
      margin: 0;
      padding: 0;
      width: 100%;
      height: 100%;
      overflow: hidden;
      background: #0B0E13;
      font-family: Inter, Segoe UI, sans-serif;
    }
    #tv_chart_container {
      width: 100%;
      height: 100%;
    }
    #chart_status {
      position: absolute;
      inset: 0;
      display: flex;
      align-items: center;
      justify-content: center;
      color: #AFB8C1;
      font: 600 14px Inter, Segoe UI, sans-serif;
      background: #0B0E13;
    }
    #chart_root {
      position: relative;
      width: 100%;
      height: 100%;
    }
  </style>
</head>
<body>
  <div id="chart_root">
    <div id="chart_status">Loading TradingView chart...</div>
    <div id="tv_chart_container"></div>
  </div>
  <script>
    function renderTradingViewChart() {
      const status = document.getElementById('chart_status');
      if (!window.TradingView || !window.TradingView.widget) {
        status.textContent = 'TradingView script not available.';
        window.chrome.webview.postMessage({ type: 'tv-error' });
        return;
      }

      try {
        new window.TradingView.widget({{payload}});
        status.style.display = 'none';
        window.chrome.webview.postMessage({ type: 'tv-ready' });
      } catch (error) {
        status.textContent = 'Chart failed to initialize.';
        window.chrome.webview.postMessage({ type: 'tv-error' });
      }
    }

    const script = document.createElement('script');
    script.src = 'https://s3.tradingview.com/external-embedding/embed-widget-advanced-chart.js';
    script.async = true;
    script.onload = renderTradingViewChart;
    script.onerror = function() {
      document.getElementById('chart_status').textContent = 'TradingView could not be loaded. Check internet access.';
      window.chrome.webview.postMessage({ type: 'tv-error' });
    };
    document.head.appendChild(script);
  </script>
  <script>
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
                TradingViewBrowser.Visibility = Visibility.Visible;
                FallbackChartLayer.Visibility = Visibility.Collapsed;
                return;
            }

            if (message == "tv-error")
            {
                TradingViewBrowser.Visibility = Visibility.Collapsed;
                FallbackChartLayer.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            TradingViewBrowser.Visibility = Visibility.Collapsed;
            FallbackChartLayer.Visibility = Visibility.Visible;
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
