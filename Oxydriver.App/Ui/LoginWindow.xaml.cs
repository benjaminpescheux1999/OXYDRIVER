using System;
using System.Windows;
using Oxydriver.Services;

namespace Oxydriver.Ui;

public partial class LoginWindow : Window
{
    private readonly AppSettingsStore _settingsStore;
    private readonly OnlineApiClient _apiClient;
    private string _expectedPassword;

    public LoginWindow(AppSettingsStore settingsStore, OnlineApiClient apiClient)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _apiClient = apiClient;
        var settings = _settingsStore.Load();
        _expectedPassword = settings.UiPassword ?? string.Empty;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void PasswordChanged(object sender, RoutedEventArgs e)
    {
        ValidateButton.IsEnabled = !string.IsNullOrWhiteSpace(PasswordInput.Password);
        if (ErrorText.Visibility == Visibility.Visible)
            ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ValidateClicked(object sender, RoutedEventArgs e)
    {
        if (string.Equals(PasswordInput.Password, _expectedPassword, StringComparison.Ordinal))
        {
            DialogResult = true;
            Close();
            return;
        }

        ErrorText.Visibility = Visibility.Visible;
        PasswordInput.SelectAll();
        PasswordInput.Focus();
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ForgotPasswordClicked(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            "Veuillez contacter l'editeur pour reinitialiser votre mot de passe.",
            "Mot de passe oublie",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information
        );
    }

    private async void RecoverFromApiClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = _settingsStore.Load();
            var dlg = new RecoverUiPasswordWindow(current.ApiBaseUrl, current.AccessKey);
            if (dlg.ShowDialog() != true)
                return;

            current.ApiBaseUrl = dlg.ApiBaseUrl;
            current.AccessKey = dlg.AccessKey;

            ValidateButton.IsEnabled = false;
            var sync = await _apiClient.SyncAsync(current, requestUiPasswordRecovery: true);
            if (!sync.IsSuccess)
            {
                System.Windows.MessageBox.Show(
                    $"Echec de synchronisation API: {sync.Message}",
                    "Recuperation mot de passe",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning
                );
                return;
            }

            current.ApiToken = sync.ApiToken ?? current.ApiToken;
            current.ApiCapabilitiesJson = sync.CapabilitiesJson ?? current.ApiCapabilitiesJson;
            if (string.IsNullOrWhiteSpace(sync.UiPassword))
            {
                _settingsStore.Save(current);
                System.Windows.MessageBox.Show(
                    "Aucun mot de passe temporaire n'a ete fourni par l'API. Demande une reinitialisation admin puis reessaie.",
                    "Recuperation mot de passe",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information
                );
                return;
            }

            current.UiPassword = sync.UiPassword!;
            current.UiPasswordMustChange = true;
            _settingsStore.Save(current);
            _expectedPassword = current.UiPassword;
            PasswordInput.Password = current.UiPassword;
            PasswordInput.SelectAll();
            PasswordInput.Focus();
            System.Windows.MessageBox.Show(
                "Mot de passe temporaire recupere. Connecte-toi maintenant avec ce mot de passe, puis il sera obligatoire de le changer.",
                "Recuperation mot de passe",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information
            );
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Impossible de recuperer le mot de passe: {ex.Message}",
                "Recuperation mot de passe",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error
            );
        }
        finally
        {
            ValidateButton.IsEnabled = !string.IsNullOrWhiteSpace(PasswordInput.Password);
        }
    }
}
