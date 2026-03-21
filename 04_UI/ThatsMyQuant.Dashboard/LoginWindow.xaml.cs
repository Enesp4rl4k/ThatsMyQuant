using System.Windows;
using System.Windows.Input;

namespace ThatsMyQuant.Dashboard;

public partial class LoginWindow : Window
{
    public bool IsAuthenticated { get; private set; } = false;

    public LoginWindow()
    {
        InitializeComponent();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        // Mock Authentication for phase 11 user parameters
        if (EmailBox.Text == "admin@thatsmyquant.com" && PasswordBox.Password == "admin")
        {
            IsAuthenticated = true;
            this.DialogResult = true;
            this.Close();
        }
        else
        {
            ErrorText.Text = "Invalid email or password.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
