using System;
using System.Windows;

namespace Oxydriver.Ui;

public partial class ChangeUiPasswordWindow : Window
{
    public ChangeUiPasswordWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NewPasswordInput.Focus();
    }

    public string? NewPassword { get; private set; }

    private void PasswordChanged(object sender, RoutedEventArgs e)
    {
        ValidateButton.IsEnabled =
            !string.IsNullOrWhiteSpace(NewPasswordInput.Password) &&
            !string.IsNullOrWhiteSpace(ConfirmPasswordInput.Password);
        if (ErrorText.Visibility == Visibility.Visible)
            ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ValidateClicked(object sender, RoutedEventArgs e)
    {
        var newPassword = NewPasswordInput.Password?.Trim() ?? string.Empty;
        var confirmPassword = ConfirmPasswordInput.Password?.Trim() ?? string.Empty;

        if (newPassword.Length < 6)
        {
            ShowError("Le mot de passe doit contenir au moins 6 caracteres.");
            return;
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            ShowError("Les deux mots de passe ne correspondent pas.");
            return;
        }

        NewPassword = newPassword;
        DialogResult = true;
        Close();
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
