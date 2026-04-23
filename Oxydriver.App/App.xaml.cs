using System.Linq;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oxydriver.Services;
using Oxydriver.Ui;

namespace Oxydriver;

public partial class App : System.Windows.Application
{
    private const int UiSessionMinutes = 30;
    private Mutex? _singleInstanceMutex;
    private bool _isPrimaryInstance;
    private bool _isServiceMode;
    private IHost? _host;
    private TrayController? _tray;
    private ILogger<App>? _logger;
    private AppSettingsStore? _settingsStore;
    private OnlineApiClient? _apiClient;
    private OxydriverBackgroundRuntime? _backgroundRuntime;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _authTimer;
    private bool _isAuthenticated;
    private DateTime _lastAuthenticatedAtUtc;
    private const string WindowsServiceName = "OXYDRIVERService";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _isServiceMode = e.Args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));
        var mutexName = _isServiceMode
            ? @"Global\OXYDRIVER_SERVICE_INSTANCE"
            : @"Global\OXYDRIVER_UI_INSTANCE";
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out var createdNew);
        _isPrimaryInstance = createdNew;

        if (!_isPrimaryInstance)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Current.Shutdown();
            return;
        }

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
        if (_isServiceMode)
        {
            _backgroundRuntime = ActivatorUtilities.CreateInstance<OxydriverBackgroundRuntime>(_host.Services);
            await _backgroundRuntime.StartAsync();
            return;
        }

        EnsureServiceInstalledIfMissing();

        _mainWindow = _host.Services.GetRequiredService<MainWindow>();

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
        // Do not prompt for password on process start.
        // Authentication is requested only when user opens the UI window.
        _mainWindow.Hide();
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
        _mainWindow?.ViewModel.ReloadSettingsFromStore();
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
                if (_backgroundRuntime is not null)
                    await _backgroundRuntime.StopAsync();
                await _host.StopAsync();
                _host.Dispose();
            }
        }
        finally
        {
            Current.Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_isPrimaryInstance && _singleInstanceMutex is not null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { /* already released */ }
            }
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }

    private void EnsureServiceInstalledIfMissing()
    {
        try
        {
            if (IsServiceInstalled(WindowsServiceName))
                return;

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
                return;

            if (IsCurrentProcessElevated())
            {
                InstallService(exePath);
                return;
            }

            // Ask elevation once to register the service automatically at first run.
            var installCommand =
                $"sc.exe create {WindowsServiceName} binPath= \"\\\"{exePath}\\\" --service\" start= auto DisplayName= \"OXYDRIVER Service\";" +
                $" sc.exe description {WindowsServiceName} \"OXYDRIVER background runtime (gateway, tunnel, API sync).\";" +
                $" sc.exe failure {WindowsServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/20000;" +
                $" sc.exe start {WindowsServiceName}";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{installCommand}\"",
                Verb = "runas",
                UseShellExecute = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to auto-install Windows service");
        }
    }

    private static bool IsServiceInstalled(string serviceName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"query {serviceName}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit();
        return p is not null && p.ExitCode == 0;
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void InstallService(string exePath)
    {
        RunSc($"create {WindowsServiceName} binPath= \"\\\"{exePath}\\\" --service\" start= auto DisplayName= \"OXYDRIVER Service\"");
        RunSc($"description {WindowsServiceName} \"OXYDRIVER background runtime (gateway, tunnel, API sync).\"");
        RunSc($"failure {WindowsServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/20000");
        RunSc($"start {WindowsServiceName}");
    }

    private static void RunSc(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }
}

