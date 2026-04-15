using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Oxydriver.Services;

public sealed class OxydriverBackgroundRuntime
{
    private readonly AppSettingsStore _settingsStore;
    private readonly CloudflaredManager _cloudflared;
    private readonly OnlineApiClient _api;
    private readonly LocalGatewayServer _server;
    private readonly ILogger<OxydriverBackgroundRuntime> _logger;
    private readonly string _serviceLogPath;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public OxydriverBackgroundRuntime(
        AppSettingsStore settingsStore,
        CloudflaredManager cloudflared,
        OnlineApiClient api,
        LocalGatewayServer server,
        ILogger<OxydriverBackgroundRuntime> logger)
    {
        _settingsStore = settingsStore;
        _cloudflared = cloudflared;
        _api = api;
        _server = server;
        _logger = logger;
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OXYDRIVER",
            "logs");
        Directory.CreateDirectory(logsDir);
        _serviceLogPath = Path.Combine(logsDir, "service-runtime.log");
    }

    public async Task StartAsync()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        WriteServiceLog("INFO", "Background runtime starting");
        try
        {
            await RunCycleAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial background cycle failed");
            WriteServiceLog("ERROR", $"Initial cycle failed: {ex.Message}");
        }
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        _cts.Cancel();
        try
        {
            if (_loopTask is not null)
                await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            _loopTask = null;
            _cts.Dispose();
            _cts = null;
            WriteServiceLog("INFO", "Background runtime stopped");
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
                await RunCycleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background cycle failed");
                WriteServiceLog("ERROR", $"Background cycle failed: {ex.Message}");
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var settings = _settingsStore.Load();
        WriteServiceLog("INFO", "Background cycle started");
        _server.UpdateAuthorizationPolicy(settings);

        var port = settings.GetLocalPortOrDefault();
        var boundPort = await _server.EnsureStartedAsync(port).ConfigureAwait(false);
        if (boundPort != port)
        {
            settings.LocalPort = boundPort.ToString();
            _settingsStore.Save(settings);
            WriteServiceLog("WARN", $"Configured port {port} unavailable. Switched to {boundPort}.");
        }

        if (!string.Equals(settings.ExposureMode, "ManualUrl", StringComparison.OrdinalIgnoreCase))
        {
            var tunnel = await _cloudflared.EnsureInstalledAndStartAsync(boundPort, forceRestart: false, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(tunnel.PublicUrl))
            {
                settings.TunnelPublicUrl = tunnel.PublicUrl;
                settings.ExposureProvider = "cloudflare";
                _settingsStore.Save(settings);
                WriteServiceLog("INFO", $"Tunnel URL active: {tunnel.PublicUrl}");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.ApiBaseUrl) && !string.IsNullOrWhiteSpace(settings.AccessKey))
        {
            var sync = await _api.SyncAsync(settings, ct: ct).ConfigureAwait(false);
            if (sync.IsSuccess)
            {
                settings.ApiToken = sync.ApiToken ?? settings.ApiToken;
                settings.ApiCapabilitiesJson = sync.CapabilitiesJson ?? settings.ApiCapabilitiesJson;
                var effectiveFolders = sync.TokenFolders.Length > 0 ? sync.TokenFolders : sync.SelectedFolders;
                if (effectiveFolders.Length > 0)
                {
                    var normalized = effectiveFolders
                        .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    settings.SelectedFoldersJson = JsonSerializer.Serialize(normalized);
                }
                if (!string.IsNullOrWhiteSpace(sync.FeatureCatalogJson))
                    settings.ApiFeatureCatalogJson = sync.FeatureCatalogJson;
                _settingsStore.Save(settings);
                WriteServiceLog("INFO", "Background sync succeeded");
            }
            else
            {
                _logger.LogWarning("Background sync refused: {Message}", sync.Message);
                WriteServiceLog("WARN", $"Background sync refused: {sync.Message}");
            }
        }
        else
        {
            WriteServiceLog("WARN", "Background sync skipped: missing ApiBaseUrl or AccessKey");
        }
    }

    private void WriteServiceLog(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(_serviceLogPath, line, Encoding.UTF8);
        }
        catch
        {
            // Logging must never crash service runtime.
        }
    }
}
