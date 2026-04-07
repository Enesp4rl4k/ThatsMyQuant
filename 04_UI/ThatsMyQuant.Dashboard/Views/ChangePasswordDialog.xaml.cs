using System.Windows;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

namespace ThatsMyQuant.Dashboard.Views;

public partial class ChangePasswordDialog : Window
{
    private readonly ChangePasswordDialogViewModel _vm;

    public ChangePasswordDialog(ChangePasswordDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;

        // Wire PasswordBox.PasswordChanged — PasswordBox cannot use standard binding.
        NewPasswordBox.PasswordChanged += (_, _) => _vm.NewPassword = NewPasswordBox.Password;
        ConfirmPasswordBox.PasswordChanged += (_, _) => _vm.ConfirmPassword = ConfirmPasswordBox.Password;

        // Give the ViewModel a way to close the dialog.
        _vm.CloseAction = result =>
        {
            DialogResult = result;
            Close();
        };
    }
}
