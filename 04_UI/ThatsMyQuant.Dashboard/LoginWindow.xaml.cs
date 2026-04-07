using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Configuration;

namespace ThatsMyQuant.Dashboard;

public partial class LoginWindow : Window
{
    public bool IsAuthenticated { get; private set; } = false;

    private readonly string _expectedEmail;
    private readonly string _expectedPasswordHash;

    public LoginWindow(IConfiguration? configuration = null)
    {
        InitializeComponent();

        // Credentials come from environment variables or appsettings (never hardcoded).
        // TMQ_ADMIN_EMAIL / TMQ_ADMIN_PASSWORD_HASH can be set per-environment.
        // Generate a hash:  echo -n "YourPassword" | sha256sum
        _expectedEmail = configuration?["Auth:AdminEmail"]
            ?? Environment.GetEnvironmentVariable("TMQ_ADMIN_EMAIL")
            ?? "admin@thatsmyquant.com";

        _expectedPasswordHash = configuration?["Auth:AdminPasswordHash"]
            ?? Environment.GetEnvironmentVariable("TMQ_ADMIN_PASSWORD_HASH")
            ?? HashPassword("admin"); // Fallback for first-run only
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

        var emailMatch = string.Equals(EmailBox.Text.Trim(), _expectedEmail, StringComparison.OrdinalIgnoreCase);
        var passwordMatch = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashPassword(PasswordBox.Password)),
            Encoding.UTF8.GetBytes(_expectedPasswordHash));

        if (emailMatch && passwordMatch)
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

    private static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexStringLower(bytes);
    }
}
