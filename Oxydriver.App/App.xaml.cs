using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oxydriver.Services;
using Oxydriver.Ui;

namespace Oxydriver;

public partial class App : System.Windows.Application
{
    private const int UiSessionMinutes = 30;
    private IHost? _host;
    private TrayController? _tray;
    private ILogger<App>? _logger;
    private AppSettingsStore? _settingsStore;
    private OnlineApiClient? _apiClient;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _authTimer;
    private bool _isAuthenticated;
    private DateTime _lastAuthenticatedAtUtc;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<AppSettingsStore>();
                services.AddSingleton<CloudflaredManager>();
                services.AddSingleton<OnlineApiClient>();
                services.AddSingleton<LocalGatewayServer>();
                services.AddSingleton<StartupIntegration>();
                services.AddSingleton<SettingsBackupService>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _settingsStore = _host.Services.GetRequiredService<AppSettingsStore>();
        _apiClient = _host.Services.GetRequiredService<OnlineApiClient>();
        _mainWindow = _host.Services.GetRequiredService<MainWindow>();

        var authenticated = EnsureAuthenticated(forcePrompt: true);
        if (!authenticated)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Current.Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unhandled UI exception");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _logger.LogCritical(ex, "Unhandled domain exception");
        };

        _tray = new TrayController(
            _mainWindow,
            ShutdownApp,
            () => EnsureAuthenticated(forcePrompt: false)
        );
        _tray.Start();
        StartAuthTimer();
        _mainWindow.Show();
    }

    private void StartAuthTimer()
    {
        _authTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _authTimer.Tick += (_, _) =>
        {
            if (!_isAuthenticated || _mainWindow is null || !_mainWindow.IsVisible)
                return;
            if (!IsSessionExpired()) return;

            // Session expirée: redemande le mot de passe si la fenêtre est ouverte.
            if (!EnsureAuthenticated(forcePrompt: true))
                _mainWindow.Hide();
        };
        _authTimer.Start();
    }

    private bool IsSessionExpired()
        => DateTime.UtcNow - _lastAuthenticatedAtUtc > TimeSpan.FromMinutes(UiSessionMinutes);

    private bool EnsureAuthenticated(bool forcePrompt)
    {
        if (_settingsStore is null || _apiClient is null)
            return false;
        if (!forcePrompt && _isAuthenticated && !IsSessionExpired())
            return true;

        var settings = _settingsStore.Load();
        var login = new LoginWindow(_settingsStore, _apiClient);
        var ok = login.ShowDialog() == true;
        if (!ok)
        {
            _isAuthenticated = false;
            return false;
        }

        if (settings.UiPasswordMustChange && !EnsurePasswordUpdated(settings))
        {
            _isAuthenticated = false;
            return false;
        }

        _isAuthenticated = true;
        _lastAuthenticatedAtUtc = DateTime.UtcNow;
        return true;
    }

    private bool EnsurePasswordUpdated(AppSettings settings)
    {
        if (_settingsStore is null)
            return false;

        var changeWindow = new ChangeUiPasswordWindow();
        var changed = changeWindow.ShowDialog() == true && !string.IsNullOrWhiteSpace(changeWindow.NewPassword);
        if (!changed)
            return false;

        settings.UiPassword = changeWindow.NewPassword!;
        settings.UiPasswordMustChange = false;
        _settingsStore.Save(settings);
        return true;
    }

    private async void ShutdownApp()
    {
        try
        {
            _authTimer?.Stop();
            _tray?.Dispose();
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }
        finally
        {
            Current.Shutdown();
        }
    }
}

