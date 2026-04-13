using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oxydriver.Services;

public sealed class OnlineApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public OnlineApiClient()
    {
        _http = new HttpClient();
    }

    public async Task<UpdateCheckResult> CheckUpdateAsync(string apiBaseUrl, string currentVersion, string accessKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return UpdateCheckResult.Fail("URL API manquante.");
        var baseUrl = apiBaseUrl.TrimEnd('/');
        var safeAccessKey = (accessKey ?? string.Empty).Trim();
        var url = $"{baseUrl}/system/utility-update?currentVersion={Uri.EscapeDataString(currentVersion)}&accessKey={Uri.EscapeDataString(safeAccessKey)}";
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return UpdateCheckResult.Fail($"HTTP {(int)resp.StatusCode}: {body}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var hasUpdate = root.TryGetProperty("hasUpdate", out var hu) && hu.GetBoolean();
            var latest = root.TryGetProperty("latestVersion", out var lv) ? lv.GetString() : null;
            var download = root.TryGetProperty("downloadUrl", out var du) ? du.GetString() : null;
            var notes = root.TryGetProperty("releaseNotesUrl", out var nu) ? nu.GetString() : null;
            download = NormalizeUrl(apiBaseUrl, download);
            notes = NormalizeUrl(apiBaseUrl, notes);
            SftpConnectionInfo? sftp = null;
            if (root.TryGetProperty("encryptedSftp", out var enc) && enc.ValueKind == JsonValueKind.Object)
                sftp = TryDecryptSftp(enc, safeAccessKey);
            return UpdateCheckResult.Ok(hasUpdate, latest, download, notes, sftp);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail($"Réponse update invalide: {ex.Message}");
        }
    }

    public async Task<FeatureDefinition[]?> GetFeatureCatalogForVersionAsync(AppSettings settings, string utilityVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl))
            return null;
        if (string.IsNullOrWhiteSpace(settings.AccessKey))
            return null;
        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var url = baseUrl + "/utility/negotiate";
        var payload = new
        {
            utilityVersion = utilityVersion.Trim(),
            accessKey = settings.AccessKey.Trim(),
            selectedFolders = ParseSelectedFolders(settings.SelectedFoldersJson)
        };
        using var resp = await _http.PostAsJsonAsync(url, payload, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("featureCatalog", out var featureCatalog) || featureCatalog.ValueKind != JsonValueKind.Array)
                return null;
            return JsonSerializer.Deserialize<FeatureDefinition[]>(featureCatalog.GetRawText(), JsonOptions) ?? [];
        }
        catch
        {
            return null;
        }
    }

    public async Task<SyncResult> SyncAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl))
            return SyncResult.Fail("URL API manquante.");
        if (string.IsNullOrWhiteSpace(settings.AccessKey))
            return SyncResult.Fail("Clé d'accès manquante.");

        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var negotiateUrl = baseUrl + "/utility/negotiate";
        var syncUrl = baseUrl + "/utility/sync";

        var negotiatePayload = new
        {
            utilityVersion = settings.AppVersion,
            accessKey = settings.AccessKey.Trim()
        };

        using var negotiateResp = await _http.PostAsJsonAsync(negotiateUrl, negotiatePayload, JsonOptions, ct);
        var negotiateBody = await negotiateResp.Content.ReadAsStringAsync(ct);
        if (!negotiateResp.IsSuccessStatusCode)
            return SyncResult.Fail($"Negotiation HTTP {(int)negotiateResp.StatusCode}: {negotiateBody}");

        string? apiVersion = null;
        try
        {
            using var negotiateDoc = JsonDocument.Parse(negotiateBody);
            if (negotiateDoc.RootElement.TryGetProperty("apiVersion", out var av))
                apiVersion = av.GetString();
        }
        catch
        {
            return SyncResult.Fail("Réponse négociation invalide.");
        }
        if (string.IsNullOrWhiteSpace(apiVersion))
            return SyncResult.Fail("Version API non retournée par la négociation.");

        var syncPayload = new
        {
            app = "OXYDRIVER",
            utilityVersion = settings.AppVersion,
            accessKey = settings.AccessKey.Trim(),
            tunnelUrl = settings.TunnelPublicUrl,
            exposureProvider = ResolveExposureProvider(settings),
            selectedFeatures = ParseEnabledFeatures(settings.EnabledFeatureCodesJson),
            selectedFolders = ParseSelectedFolders(settings.SelectedFoldersJson),
            capabilities = new
            {
                incomingCalls = true,
                read = true,
                write = true
            }
        };

        using var syncResp = await _http.PostAsJsonAsync(syncUrl, syncPayload, JsonOptions, ct);
        var syncBody = await syncResp.Content.ReadAsStringAsync(ct);
        if (!syncResp.IsSuccessStatusCode)
            return SyncResult.Fail($"Sync HTTP {(int)syncResp.StatusCode}: {syncBody}");

        try
        {
            using var doc = JsonDocument.Parse(syncBody);
            var root = doc.RootElement;
            var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
            var caps = root.TryGetProperty("capabilities", out var c) ? c.GetRawText() : null;
            var featureCatalog = root.TryGetProperty("featureCatalog", out var f) ? f.GetRawText() : null;
            var selectedFolders = root.TryGetProperty("selectedFolders", out var sf) ? ParseStringArray(sf) : Array.Empty<string>();
            var tokenFolders = root.TryGetProperty("tokenFolders", out var tf) ? ParseStringArray(tf) : Array.Empty<string>();
            var hasUpdate = false;
            string? latestVersion = null;
            string? downloadUrl = null;
            string? releaseNotesUrl = null;
            SftpConnectionInfo? sftp = null;
            if (root.TryGetProperty("update", out var update) && update.ValueKind == JsonValueKind.Object)
            {
                hasUpdate = update.TryGetProperty("hasUpdate", out var hu) && hu.GetBoolean();
                latestVersion = update.TryGetProperty("latestVersion", out var lv) ? lv.GetString() : null;
                downloadUrl = update.TryGetProperty("downloadUrl", out var du) ? du.GetString() : null;
                releaseNotesUrl = update.TryGetProperty("releaseNotesUrl", out var nu) ? nu.GetString() : null;
                downloadUrl = NormalizeUrl(apiBaseUrl: settings.ApiBaseUrl, url: downloadUrl);
                releaseNotesUrl = NormalizeUrl(apiBaseUrl: settings.ApiBaseUrl, url: releaseNotesUrl);
                if (update.TryGetProperty("encryptedSftp", out var enc) && enc.ValueKind == JsonValueKind.Object)
                    sftp = TryDecryptSftp(enc, settings.AccessKey.Trim());
            }
            return SyncResult.Ok(token, caps, apiVersion, featureCatalog, hasUpdate, latestVersion, downloadUrl, releaseNotesUrl, sftp, selectedFolders, tokenFolders);
        }
        catch (Exception ex)
        {
            return SyncResult.Fail($"Réponse API invalide: {ex.Message}");
        }
    }

    private static string[] ParseStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return element
            .EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveExposureProvider(AppSettings settings)
    {
        var mode = (settings.ExposureMode ?? string.Empty).Trim();
        if (string.Equals(mode, "ManualUrl", StringComparison.OrdinalIgnoreCase))
        {
            var manual = (settings.ExposureProvider ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(manual) ? "manual" : manual.ToLowerInvariant();
        }
        return "cloudflare";
    }

    private static IEnumerable<string> ParseEnabledFeatures(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> ParseSelectedFolders(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>();
            return values
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? NormalizeUrl(string apiBaseUrl, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
            return url;

        var baseUrl = apiBaseUrl.TrimEnd('/');
        if (url.StartsWith('/'))
            return $"{baseUrl}{url}";
        return $"{baseUrl}/{url}";
    }

    private static SftpConnectionInfo? TryDecryptSftp(JsonElement encrypted, string accessKey)
    {
        try
        {
            var alg = encrypted.GetProperty("alg").GetString();
            if (!string.Equals(alg, "aes-256-gcm", StringComparison.OrdinalIgnoreCase))
                return null;
            var iterations = encrypted.GetProperty("iterations").GetInt32();
            var salt = Convert.FromBase64String(encrypted.GetProperty("salt").GetString() ?? "");
            var iv = Convert.FromBase64String(encrypted.GetProperty("iv").GetString() ?? "");
            var tag = Convert.FromBase64String(encrypted.GetProperty("tag").GetString() ?? "");
            var ciphertext = Convert.FromBase64String(encrypted.GetProperty("ciphertext").GetString() ?? "");
            var key = Rfc2898DeriveBytes.Pbkdf2(accessKey, salt, iterations, HashAlgorithmName.SHA256, 32);
            var plain = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(iv, ciphertext, tag, plain);
            using var doc = JsonDocument.Parse(plain);
            var root = doc.RootElement;
            return new SftpConnectionInfo(
                root.TryGetProperty("host", out var host) ? host.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("port", out var port) ? port.GetInt32() : 22,
                root.TryGetProperty("username", out var user) ? user.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("password", out var pwd) ? pwd.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("remotePath", out var rp) ? rp.GetString() ?? string.Empty : string.Empty
            );
        }
        catch
        {
            return null;
        }
    }

    public async Task<BindClientTokenResult> BindClientTokenAsync(string apiBaseUrl, string accessKey, string clientToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return BindClientTokenResult.Fail("URL API manquante.");
        if (string.IsNullOrWhiteSpace(accessKey))
            return BindClientTokenResult.Fail("Clé d'accès manquante.");
        if (string.IsNullOrWhiteSpace(clientToken))
            return BindClientTokenResult.Fail("ClientToken manquant.");

        var baseUrl = apiBaseUrl.TrimEnd('/');
        var url = baseUrl + "/client/bind";
        var payload = new { clientToken, accessKey };
        using var resp = await _http.PostAsJsonAsync(url, payload, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return BindClientTokenResult.Fail($"HTTP {(int)resp.StatusCode}: {body}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok) return BindClientTokenResult.Fail("Réponse bind invalide.");
            var clientId = root.TryGetProperty("clientId", out var cid) ? cid.GetString() : null;
            var utilityId = root.TryGetProperty("utilityId", out var uid) ? uid.GetString() : null;
            return BindClientTokenResult.Ok(clientId, utilityId);
        }
        catch (Exception ex)
        {
            return BindClientTokenResult.Fail($"Réponse bind invalide: {ex.Message}");
        }
    }

    public async Task<RotateClientTokenResult> RotateClientTokenAsync(string apiBaseUrl, string accessKey, string newClientToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return RotateClientTokenResult.Fail("URL API manquante.");
        if (string.IsNullOrWhiteSpace(accessKey))
            return RotateClientTokenResult.Fail("Clé d'accès manquante.");
        if (string.IsNullOrWhiteSpace(newClientToken))
            return RotateClientTokenResult.Fail("Nouveau ClientToken manquant.");

        var baseUrl = apiBaseUrl.TrimEnd('/');
        var url = baseUrl + "/client/rotate";
        var payload = new { accessKey, newClientToken };
        using var resp = await _http.PostAsJsonAsync(url, payload, JsonOptions, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return RotateClientTokenResult.Fail($"HTTP {(int)resp.StatusCode}: {body}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok) return RotateClientTokenResult.Fail("Réponse rotate invalide.");
            var deletedCount = root.TryGetProperty("deletedCount", out var dc) ? dc.GetInt32() : 0;
            var clientId = root.TryGetProperty("clientId", out var cid) ? cid.GetString() : null;
            var utilityId = root.TryGetProperty("utilityId", out var uid) ? uid.GetString() : null;
            return RotateClientTokenResult.Ok(deletedCount, clientId, utilityId);
        }
        catch (Exception ex)
        {
            return RotateClientTokenResult.Fail($"Réponse rotate invalide: {ex.Message}");
        }
    }
}

public sealed record SyncResult(bool IsSuccess, string Message, string? ApiToken, string? CapabilitiesJson, string? ApiVersion)
{
    public static SyncResult Ok(
        string? token,
        string? capabilitiesJson,
        string? apiVersion,
        string? featureCatalogJson,
        bool hasUpdate,
        string? latestVersion,
        string? downloadUrl,
        string? releaseNotesUrl,
        SftpConnectionInfo? sftp,
        string[]? selectedFolders,
        string[]? tokenFolders) =>
        new(true, "OK", token, capabilitiesJson, apiVersion)
        {
            FeatureCatalogJson = featureCatalogJson,
            HasUpdate = hasUpdate,
            LatestVersion = latestVersion,
            DownloadUrl = downloadUrl,
            ReleaseNotesUrl = releaseNotesUrl,
            Sftp = sftp,
            SelectedFolders = selectedFolders ?? Array.Empty<string>(),
            TokenFolders = tokenFolders ?? Array.Empty<string>()
        };

    public static SyncResult Fail(string message) =>
        new(false, message, null, null, null);

    public string? FeatureCatalogJson { get; init; }
    public bool HasUpdate { get; init; }
    public string? LatestVersion { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public SftpConnectionInfo? Sftp { get; init; }
    public string[] SelectedFolders { get; init; } = Array.Empty<string>();
    public string[] TokenFolders { get; init; } = Array.Empty<string>();
}

public sealed record UpdateCheckResult(bool IsSuccess, string Message, bool HasUpdate, string? LatestVersion, string? DownloadUrl, string? ReleaseNotesUrl)
{
    public static UpdateCheckResult Ok(bool hasUpdate, string? latestVersion, string? downloadUrl, string? notesUrl, SftpConnectionInfo? sftp) =>
        new(true, "OK", hasUpdate, latestVersion, downloadUrl, notesUrl) { Sftp = sftp };
    public static UpdateCheckResult Fail(string message) =>
        new(false, message, false, null, null, null);

    public SftpConnectionInfo? Sftp { get; init; }
}

public sealed record SftpConnectionInfo(string Host, int Port, string Username, string Password, string RemotePath);

public sealed record BindClientTokenResult(bool IsSuccess, string Message, string? ClientId, string? UtilityId)
{
    public static BindClientTokenResult Ok(string? clientId, string? utilityId) => new(true, "OK", clientId, utilityId);
    public static BindClientTokenResult Fail(string message) => new(false, message, null, null);
}

public sealed record RotateClientTokenResult(bool IsSuccess, string Message, int DeletedCount, string? ClientId, string? UtilityId)
{
    public static RotateClientTokenResult Ok(int deletedCount, string? clientId, string? utilityId) => new(true, "OK", deletedCount, clientId, utilityId);
    public static RotateClientTokenResult Fail(string message) => new(false, message, 0, null, null);
}

