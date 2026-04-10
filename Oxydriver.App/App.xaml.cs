using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oxydriver.Services;
using Oxydriver.Ui;

namespace Oxydriver;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayController? _tray;
    private ILogger<App>? _logger;

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
            _host.Services.GetRequiredService<MainWindow>(),
            ShutdownApp
        );
        _tray.Start();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    private async void ShutdownApp()
    {
        try
        {
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

