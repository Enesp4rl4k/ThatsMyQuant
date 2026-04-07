using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

// Requirements: 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7
public partial class UserManagementViewModel : ObservableObject
{
    private readonly IUserAuthService _authService;

    [ObservableProperty]
    private ObservableCollection<OperatorUser> _users = new();

    [ObservableProperty]
    private OperatorUser? _selectedUser;

    public IAsyncRelayCommand LoadUsersCommand    { get; }
    public IAsyncRelayCommand AddUserCommand      { get; }
    public IAsyncRelayCommand ResetPasswordCommand { get; }
    public IAsyncRelayCommand ToggleActiveCommand  { get; }
    public IAsyncRelayCommand DeleteUserCommand    { get; }

    public UserManagementViewModel(IUserAuthService authService)
    {
        _authService = authService;

        LoadUsersCommand     = new AsyncRelayCommand(LoadUsersAsync);
        AddUserCommand       = new AsyncRelayCommand(AddUserAsync);
        ResetPasswordCommand = new AsyncRelayCommand(ResetPasswordAsync, () => SelectedUser != null);
        ToggleActiveCommand  = new AsyncRelayCommand(ToggleActiveAsync,  () => SelectedUser != null);
        DeleteUserCommand    = new AsyncRelayCommand(DeleteUserAsync,    () => SelectedUser != null);
    }

    partial void OnSelectedUserChanged(OperatorUser? value)
    {
        ResetPasswordCommand.NotifyCanExecuteChanged();
        ToggleActiveCommand.NotifyCanExecuteChanged();
        DeleteUserCommand.NotifyCanExecuteChanged();
    }

    // Requirement 9.1 — list all users
    private async Task LoadUsersAsync()
    {
        var list = await _authService.ListUsersAsync();
        Users = new ObservableCollection<OperatorUser>(list);
    }

    // Requirement 9.2 — create a new user
    private async Task AddUserAsync()
    {
        string? username = PromptInput("Add User", "Username:");
        if (string.IsNullOrWhiteSpace(username)) return;

        string? email = PromptInput("Add User", "Email:");
        if (string.IsNullOrWhiteSpace(email)) return;

        string? password = PromptInput("Add User", "Password:");
        if (string.IsNullOrWhiteSpace(password)) return;

        string? roleStr = PromptInput("Add User", "Role (Admin / Operator / ReadOnly):");
        if (!Enum.TryParse<Role>(roleStr, ignoreCase: true, out var role))
        {
            MessageBox.Show("Invalid role. Use Admin, Operator, or ReadOnly.",
                "Add User", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _authService.CreateUserAsync(username, email, password, role);
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Add User Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Requirement 9.3 — reset a user's password (MustChangePassword=true is a TODO:
    // IUserAuthService.UpdatePasswordAsync sets MustChangePassword=false; a dedicated
    // service method or direct DB update would be needed to force re-change on next login)
    private async Task ResetPasswordAsync()
    {
        if (SelectedUser == null) return;

        string? newPassword = PromptInput("Reset Password",
            $"New password for '{SelectedUser.Username}':");
        if (string.IsNullOrWhiteSpace(newPassword)) return;

        try
        {
            await _authService.UpdatePasswordAsync(SelectedUser.Id, newPassword);
            // TODO: set MustChangePassword=true — requires a dedicated service method
            // not currently present on IUserAuthService.
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Reset Password Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Requirements 9.4, 9.6 — toggle active state, guard last active Admin
    private async Task ToggleActiveAsync()
    {
        if (SelectedUser == null) return;

        try
        {
            await _authService.SetUserActiveAsync(SelectedUser.Id, !SelectedUser.IsActive);
            await LoadUsersAsync();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Toggle Active Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Requirements 9.5, 9.6 — delete user with confirmation, guard last active Admin
    private async Task DeleteUserAsync()
    {
        if (SelectedUser == null) return;

        var confirm = MessageBox.Show(
            $"Delete user '{SelectedUser.Username}'? This cannot be undone.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _authService.DeleteUserAsync(SelectedUser.Id);
            await LoadUsersAsync();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Minimal single-field WPF input dialog (avoids Microsoft.VisualBasic dependency)
    private static string? PromptInput(string title, string prompt)
    {
        string? result = null;

        var win = new Window
        {
            Title           = title,
            Width           = 360,
            Height          = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode      = ResizeMode.NoResize,
            Owner           = System.Windows.Application.Current.MainWindow
        };

        var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        var label = new System.Windows.Controls.TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) };
        var box   = new System.Windows.Controls.TextBox   { Margin = new Thickness(0, 0, 0, 10) };
        var btn   = new System.Windows.Controls.Button    { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };

        btn.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        stack.Children.Add(label);
        stack.Children.Add(box);
        stack.Children.Add(btn);
        win.Content = stack;
        win.Loaded += (_, _) => box.Focus();

        win.ShowDialog();
        return string.IsNullOrEmpty(result) ? null : result;
    }
}
