using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Dashboard.Auth;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public partial class ChangePasswordDialogViewModel : ObservableObject
{
    private readonly IUserAuthService _authService;

    [ObservableProperty] private string? _newPassword;
    [ObservableProperty] private string? _confirmPassword;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>
    /// Set by the code-behind so the ViewModel can close the dialog
    /// without a direct Window reference. Receives true on success, false on cancel.
    /// </summary>
    public Action<bool>? CloseAction { get; set; }

    public ChangePasswordDialogViewModel(IUserAuthService authService)
    {
        _authService = authService;
    }

    [RelayCommand]
    private async Task Submit()
    {
        ErrorMessage = null;

        var validationError = PasswordValidator.Validate(NewPassword ?? string.Empty);
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            return;
        }

        try
        {
            var current = CurrentSession.Current;
            if (current is null)
            {
                ErrorMessage = "Session expired. Please sign in again.";
                return;
            }

            await _authService.UpdatePasswordAsync(current.Id, NewPassword!);
            CloseAction?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseAction?.Invoke(false);
    }
}
