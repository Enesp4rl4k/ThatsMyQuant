using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace ThatsMyQuant.Dashboard.Controls;

public partial class MarketAnalyticsControl : UserControl
{
    private bool _isInitialized;

    public MarketAnalyticsControl()
    {
        InitializeComponent();
        Loaded += MarketAnalyticsControl_Loaded;
    }

    private async void MarketAnalyticsControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;

        try
        {
            await ReactWebView.EnsureCoreWebView2Async();
            ReactWebView.CoreWebView2.NavigationCompleted += (s, args) =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            };
            
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize WebView2: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
