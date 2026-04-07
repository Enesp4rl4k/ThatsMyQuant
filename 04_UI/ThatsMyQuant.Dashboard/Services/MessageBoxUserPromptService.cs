using System.Windows;

namespace ThatsMyQuant.Dashboard.Services;

public sealed class MessageBoxUserPromptService : IUserPromptService
{
    public bool Confirm(string title, string message)
    {
        return MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }
}
