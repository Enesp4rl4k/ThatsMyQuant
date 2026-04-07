using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ThatsMyQuant.Launcher;

public sealed class LauncherServiceStatus : INotifyPropertyChanged
{
    private string _state = "Stopped";
    private string _details = "Not running";
    private Brush _stateBrush = Brushes.Gray;

    public LauncherServiceStatus(LauncherServiceDefinition definition)
    {
        Definition = definition;
    }

    public LauncherServiceDefinition Definition { get; }

    public string DisplayName => Definition.DisplayName;

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public string Details
    {
        get => _details;
        set => SetField(ref _details, value);
    }

    public Brush StateBrush
    {
        get => _stateBrush;
        set => SetField(ref _stateBrush, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
