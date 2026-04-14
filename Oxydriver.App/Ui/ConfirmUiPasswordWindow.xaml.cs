using System.Windows;

namespace Oxydriver.Ui;

public partial class ConfirmUiPasswordWindow : Window
{
    public ConfirmUiPasswordWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public string EnteredPassword { get; private set; } = string.Empty;

    private void PasswordChanged(object sender, RoutedEventArgs e)
    {
        ValidateButton.IsEnabled = !string.IsNullOrWhiteSpace(PasswordInput.Password);
        if (ErrorText.Visibility == Visibility.Visible)
            ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ValidateClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PasswordInput.Password))
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        EnteredPassword = PasswordInput.Password;
        DialogResult = true;
        Close();
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
