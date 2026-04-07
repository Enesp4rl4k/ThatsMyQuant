using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using System.IO;

namespace ThatsMyQuant.Dashboard.Controls;

public partial class MarketAnalyticsControl : UserControl
{
    private bool _isInitialized;
    private bool _eventsAttached;

    public MarketAnalyticsControl()
    {
        InitializeComponent();
        Loaded += MarketAnalyticsControl_Loaded;
    }

    private async void MarketAnalyticsControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;

        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await ReactWebView.EnsureCoreWebView2Async();

            if (!_eventsAttached)
            {
                ReactWebView.CoreWebView2.NavigationCompleted += ReactWebView_NavigationCompleted;
                _eventsAttached = true;
            }

            var analyticsUri = ResolveAnalyticsUri();
            if (analyticsUri == null)
            {
                ShowFallback(
                    "No analytics endpoint is available for the embedded panel.",
                    "Set TMQ_MARKET_ANALYTICS_URL or build 04_UI/ThatsMyQuant.Web before launching the dashboard.");
                return;
            }

            FallbackOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Visible;
            ReactWebView.Source = analyticsUri;
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            ShowFallback("WebView2 failed to initialize for the analytics panel.", ex.Message);
            _isInitialized = false;
        }
    }

    private void ReactWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            FallbackOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        ShowFallback("The analytics surface did not finish loading.", $"WebView2 status: {e.WebErrorStatus}");
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        _isInitialized = false;
        _ = InitializeAsync();
    }

    private void ShowFallback(string message, string detail)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        FallbackOverlay.Visibility = Visibility.Visible;
        FallbackMessageText.Text = message;
        FallbackSourceText.Text = detail;
    }

    private static Uri? ResolveAnalyticsUri()
    {
        var configured = Environment.GetEnvironmentVariable("TMQ_MARKET_ANALYTICS_URL");
        if (!string.IsNullOrWhiteSpace(configured) &&
            Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri))
        {
            return configuredUri;
        }

        var localDist = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "04_UI", "ThatsMyQuant.Web", "dist", "index.html"));
        if (File.Exists(localDist))
        {
            return new Uri(localDist);
        }

        return Uri.TryCreate("http://localhost:5173", UriKind.Absolute, out var localDevUri)
            ? localDevUri
            : null;
    }
}
