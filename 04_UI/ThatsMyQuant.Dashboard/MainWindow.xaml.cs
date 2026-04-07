using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using ThatsMyQuant.Dashboard.Services;
using ThatsMyQuant.Dashboard.ViewModels;

namespace ThatsMyQuant.Dashboard;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private readonly IdleTimeoutService? _idleTimeoutService;

    public MainWindow(MainViewModel viewModel, IdleTimeoutService? idleTimeoutService = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _idleTimeoutService = idleTimeoutService;

        Loaded += MainWindow_Loaded;
        KeyDown += MainWindow_KeyDown;

        if (_idleTimeoutService != null)
        {
            MouseMove  += (_, _) => _idleTimeoutService.IdleMonitor.Reset();
            MouseDown  += (_, _) => _idleTimeoutService.IdleMonitor.Reset();
        }
    }

    private void MainWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Reset idle timer on any key press (Req 3.1, 3.2)
        _idleTimeoutService?.IdleMonitor.Reset();

        if (e.Key == System.Windows.Input.Key.F11)
        {
            ToggleFullScreen();
        }
        else if (e.Key == System.Windows.Input.Key.Z)
        {
            if (ViewModel.ToggleZenModeCommand.CanExecute(null))
            {
                ViewModel.ToggleZenModeCommand.Execute(null);
            }
        }
    }

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        try
        {
            await Task.Yield();
            await ViewModel.RefreshAnalysisCommand.ExecuteAsync(null);
            await ViewModel.RefreshStrategyLeaderboardAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Initial dashboard refresh failed, but the shell will stay open.\n\n{ex.Message}",
                "ThatsMyQuant Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        // Start idle monitoring after the window is fully loaded (Req 3.1, 3.2)
        _idleTimeoutService?.Start();
    }


    private void NavigateTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string targetHeader)
        {
            return;
        }

        foreach (var item in MainTabs.Items)
        {
            if (item is TabItem tab && string.Equals(tab.Header?.ToString(), targetHeader, StringComparison.Ordinal))
            {
                MainTabs.SelectedItem = tab;
                break;
            }
        }
    }
}
