using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class ChecklistItemViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isPassing;
    [ObservableProperty] private Brush _color = Brushes.Gray;
}
