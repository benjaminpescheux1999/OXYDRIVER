using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Oxydriver.Services;

public sealed class CloudflaredManager
{
    private static readonly Regex PublicUrlRegex = new(@"https://[a-zA-Z0-9\-\.]+", RegexOptions.Compiled);
    private static readonly Regex TryCloudflareRegex = new(@"^https://[a-z0-9\-]+\.trycloudflare\.com/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly string _installDir;
    private readonly string _exePath;
    private Process? _process;
    private string? _lastPublicUrl;

    public CloudflaredManager()
    {
        _installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OXYDRIVER", "bin");
        Directory.CreateDirectory(_installDir);
        _exePath = Path.Combine(_installDir, "cloudflared.exe");
    }

    public async Task<TunnelStartResult> EnsureInstalledAndStartAsync(int localPort, bool forceRestart = false, CancellationToken ct = default)
    {
        await EnsureInstalledAsync(ct);
        var publicUrl = await StartAndGetPublicUrlAsync(localPort, forceRestart, ct);
        var status = string.IsNullOrWhiteSpace(publicUrl)
            ? $"en cours (localhost:{localPort})"
            : $"en cours (localhost:{localPort}) - {publicUrl}";
        return new TunnelStartResult(true, status, publicUrl);
    }

    private async Task EnsureInstalledAsync(CancellationToken ct)
    {
        if (File.Exists(_exePath))
            return;

        // Cloudflare official "latest" binary for Windows amd64
        var downloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

        using var http = new HttpClient();
        using var resp = await http.GetAsync(downloadUrl, ct);
        resp.EnsureSuccessStatusCode();

        await using var fs = new FileStream(_exePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await resp.Content.CopyToAsync(fs, ct);
    }

    private async Task<string?> StartAndGetPublicUrlAsync(int localPort, bool forceRestart, CancellationToken ct)
    {
        if (_process is { HasExited: false })
        {
            if (!forceRestart)
                return _lastPublicUrl;
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
            _process = null;
        }

        // Mode MVP: tunnel "quick" (sans config). Pour production, préférer un tunnel nommé + credentials.
        var args = $"tunnel --no-autoupdate --url http://127.0.0.1:{localPort}";

        // Force a fresh capture from process output
        _lastPublicUrl = null;

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.OutputDataReceived += (_, e) => TryCapturePublicUrl(e.Data, tcs);
        _process.ErrorDataReceived += (_, e) => TryCapturePublicUrl(e.Data, tcs);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await using var _ = timeoutCts.Token.Register(() => tcs.TrySetResult(null));
            var detected = await tcs.Task.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(detected))
                _lastPublicUrl = detected;
            return detected;
        }
        catch
        {
            return _lastPublicUrl;
        }
    }

    private void TryCapturePublicUrl(string? line, TaskCompletionSource<string?> tcs)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        foreach (Match m in PublicUrlRegex.Matches(line))
        {
            var url = m.Value.Trim();
            if (!TryCloudflareRegex.IsMatch(url)) continue;
            _lastPublicUrl = url.TrimEnd('/');
            tcs.TrySetResult(_lastPublicUrl);
            return;
        }
    }
}

public sealed record TunnelStartResult(bool IsSuccess, string StatusText, string? PublicUrl);

