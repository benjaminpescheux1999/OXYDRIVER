using System.Reflection;
using System.Windows;
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
        _vm = new MainWindowViewModel(settings, cloudflared, api, server, startupIntegration, backupService, Assembly.GetExecutingAssembly());
        DataContext = _vm;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Minimise-to-tray behavior by default
        e.Cancel = true;
        Hide();
    }
}
