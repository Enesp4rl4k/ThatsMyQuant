using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
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

    private static bool IsTextInputFocused()
    {
        var el = Keyboard.FocusedElement;
        return el is TextBox or PasswordBox or RichTextBox;
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        // Reset idle timer on any key press (Req 3.1, 3.2)
        _idleTimeoutService?.IdleMonitor.Reset();

        var textTyping = IsTextInputFocused();

        if (e.Key == Key.F11 && !textTyping)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key is >= Key.D1 and <= Key.D9)
            {
                SelectMainTabByIndex(e.Key - Key.D1);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.D0)
            {
                SelectMainTabByIndex(9);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Next) // PgDn
            {
                CycleMainTab(1);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Prior) // PgUp
            {
                CycleMainTab(-1);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.F5 && !textTyping)
        {
            _ = ViewModel.RefreshAnalysisCommand.ExecuteAsync(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel.IsZenMode)
        {
            if (ViewModel.ToggleZenModeCommand.CanExecute(null))
            {
                ViewModel.ToggleZenModeCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && !textTyping && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (ViewModel.ToggleZenModeCommand.CanExecute(null))
            {
                ViewModel.ToggleZenModeCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void SelectMainTabByIndex(int index)
    {
        if (MainTabs is null || index < 0 || index >= MainTabs.Items.Count)
        {
            return;
        }

        MainTabs.SelectedIndex = index;
    }

    private void CycleMainTab(int delta)
    {
        if (MainTabs is null || MainTabs.Items.Count == 0)
        {
            return;
        }

        var n = MainTabs.Items.Count;
        var next = (MainTabs.SelectedIndex + delta) % n;
        if (next < 0)
        {
            next += n;
        }

        MainTabs.SelectedIndex = next;
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
