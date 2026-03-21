using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using ThatsMyQuant.Dashboard.ViewModels;

namespace ThatsMyQuant.Dashboard;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await Dispatcher.InvokeAsync(async () =>
        {
            await ViewModel.RefreshAnalysisCommand.ExecuteAsync(null);
            await ViewModel.RefreshStrategyLeaderboardAsync();
        }, DispatcherPriority.Background);
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
