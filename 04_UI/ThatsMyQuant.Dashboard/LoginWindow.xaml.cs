using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Dashboard.Auth;
using ThatsMyQuant.Dashboard.Views;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

namespace ThatsMyQuant.Dashboard;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Guard against race where the mouse has already been released by the time
        // the event handler runs — calling DragMove() without a held button throws.
        if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { /* button released between check and call */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var authService = App.ServiceProvider!.GetRequiredService<IUserAuthService>();
            var user = await authService.AuthenticateAsync(EmailBox.Text, PasswordBox.Password);

            if (user is null)
            {
                ErrorText.Text = "Invalid credentials or account is inactive.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            CurrentSession.Current = user;

            if (user.MustChangePassword)
            {
                var vm = new ChangePasswordDialogViewModel(authService);
                var dialog = new ChangePasswordDialog(vm) { Owner = this };
                var changed = dialog.ShowDialog();
                if (changed != true)
                {
                    CurrentSession.Clear();
                    return;
                }
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Login failed: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
