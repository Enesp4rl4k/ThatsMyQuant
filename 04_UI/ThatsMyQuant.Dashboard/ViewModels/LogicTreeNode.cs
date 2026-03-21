using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class LogicTreeNode : ObservableObject
{
    [ObservableProperty] private string _category = "";
    public ObservableCollection<IndicatorDetailViewModel> Indicators { get; } = new();
}
