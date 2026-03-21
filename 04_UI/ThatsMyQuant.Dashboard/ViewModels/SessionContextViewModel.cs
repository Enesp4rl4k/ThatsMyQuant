using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class SessionContextViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _guidance = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private Brush _color = Brushes.Gray;
}
