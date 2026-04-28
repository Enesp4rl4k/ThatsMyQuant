using System.Windows.Controls;
using ThatsMyQuant.Dashboard.ViewModels;

namespace ThatsMyQuant.Dashboard.Views;

public partial class AlphaPortfolioView : UserControl
{
    public AlphaPortfolioView()
    {
        InitializeComponent();
    }

    public AlphaPortfolioView(AlphaPortfolioViewModel vm) : this()
    {
        DataContext = vm;
    }
}
