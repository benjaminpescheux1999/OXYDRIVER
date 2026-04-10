using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Oxydriver.Services;

namespace Oxydriver.Ui;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    public MainWindow(
        AppSettingsStore settings,
        CloudflaredManager cloudflared,
        OnlineApiClient api,
        LocalGatewayServer server,
        StartupIntegration startupIntegration,
        SettingsBackupService backupService
    )
    {
        InitializeComponent();
#if !DEBUG
        SftpDebugGroup.Visibility = Visibility.Collapsed;
#endif
        _vm = new MainWindowViewModel(settings, cloudflared, api, server, startupIntegration, backupService, Assembly.GetExecutingAssembly());
        DataContext = _vm;

        // PasswordBox ne supporte pas un binding TwoWay de Password.
        // On réinjecte explicitement les valeurs persistées au chargement.
        AccessKeyPasswordBox.Password = _vm.Settings.AccessKey ?? string.Empty;
        SqlPasswordBox.Password = _vm.Settings.SqlPassword ?? string.Empty;
        SftpPasswordBox.Password = _vm.Settings.SftpPassword ?? string.Empty;
    }

    private void BackupPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            _vm.BackupPassword = pb.Password;
        }
    }

    private async void ExportBackupClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter backup OXYDRIVER",
            Filter = "Backup chiffré (*.oxybak)|*.oxybak|JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*",
            FileName = $"oxydriver-backup-{DateTime.Now:yyyyMMdd-HHmmss}.oxybak"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _vm.ExportBackupAsync(dialog.FileName);
        }
    }

    private async void ImportBackupClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importer backup OXYDRIVER",
            Filter = "Backup chiffré (*.oxybak)|*.oxybak|JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _vm.ImportBackupAsync(dialog.FileName);
        }
    }

    private void AccessKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            _vm.Settings.AccessKey = pb.Password;
        }
    }

    private void SqlPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            _vm.Settings.SqlPassword = pb.Password;
        }
    }

    private void SftpPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            _vm.Settings.SftpPassword = pb.Password;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Minimise-to-tray behavior by default
        e.Cancel = true;
        Hide();
    }
}

