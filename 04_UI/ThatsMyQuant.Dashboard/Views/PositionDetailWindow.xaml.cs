using System.Windows;
using ThatsMyQuant.Dashboard.ViewModels;

namespace ThatsMyQuant.Dashboard.Views;

public partial class PositionDetailWindow : Window
{
    public PositionDetailWindow(PositionDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadCandlesCommand.ExecuteAsync(null);
    }
}
