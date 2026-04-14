using System;
using System.Windows;

namespace Oxydriver.Ui;

public partial class RecoverUiPasswordWindow : Window
{
    public RecoverUiPasswordWindow(string apiBaseUrl, string accessKey)
    {
        InitializeComponent();
        ApiBaseUrlTextBox.Text = apiBaseUrl ?? string.Empty;
        AccessKeyTextBox.Text = accessKey ?? string.Empty;
        Loaded += (_, _) => ApiBaseUrlTextBox.Focus();
    }

    public string ApiBaseUrl => (ApiBaseUrlTextBox.Text ?? string.Empty).Trim();
    public string AccessKey => (AccessKeyTextBox.Text ?? string.Empty).Trim();

    private void ValueChanged(object sender, RoutedEventArgs e)
    {
        ValidateButton.IsEnabled =
            !string.IsNullOrWhiteSpace(ApiBaseUrl) &&
            !string.IsNullOrWhiteSpace(AccessKey);
        if (ErrorText.Visibility == Visibility.Visible)
            ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ValidateClicked(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out _))
        {
            ShowError("L'URL API est invalide.");
            return;
        }
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
