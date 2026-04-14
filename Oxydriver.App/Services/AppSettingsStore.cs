using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oxydriver.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettingsStore()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OXYDRIVER"
        );
        Directory.CreateDirectory(baseDir);
        _settingsPath = Path.Combine(baseDir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            var s = new AppSettings();
            Save(s);
            return s;
        }

        var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

        // Déchiffre la clé d'accès si protégée
        settings.AccessKey = TryUnprotect(settings.AccessKey);
        settings.ApiToken = settings.ApiToken is null ? null : TryUnprotect(settings.ApiToken);
        settings.ClientToken = TryUnprotect(settings.ClientToken);
        settings.UiPassword = TryUnprotect(settings.UiPassword);
        settings.SqlPassword = TryUnprotect(settings.SqlPassword);
        settings.SqlRuntimePassword = TryUnprotect(settings.SqlRuntimePassword);
        settings.SftpPassword = TryUnprotect(settings.SftpPassword);

        return settings;
    }

    public void Save(AppSettings settings)
    {
        var copy = new AppSettings
        {
            AppVersion = settings.AppVersion,
            ApiBaseUrl = settings.ApiBaseUrl,
            AccessKey = Protect(settings.AccessKey),
            ApiToken = settings.ApiToken is null ? null : Protect(settings.ApiToken),
            ClientToken = Protect(settings.ClientToken),
            UiPassword = Protect(settings.UiPassword),
            UiPasswordMustChange = settings.UiPasswordMustChange,
            SqlConnectionString = settings.SqlConnectionString,
            SqlServerHost = settings.SqlServerHost,
            SqlAuthenticationMode = settings.SqlAuthenticationMode,
            SqlUserName = settings.SqlUserName,
            SqlPassword = Protect(settings.SqlPassword),
            SqlRuntimeUserName = settings.SqlRuntimeUserName,
            SqlRuntimePassword = Protect(settings.SqlRuntimePassword),
            SqlEncryptMode = settings.SqlEncryptMode,
            SqlTrustServerCertificate = settings.SqlTrustServerCertificate,
            SqlConnectTimeoutSeconds = settings.SqlConnectTimeoutSeconds,
            DefaultDatabase = settings.DefaultDatabase,
            LocalPort = settings.LocalPort,
            LaunchAtStartup = settings.LaunchAtStartup,
            SftpHost = settings.SftpHost,
            SftpPort = settings.SftpPort,
            SftpUsername = settings.SftpUsername,
            SftpPassword = Protect(settings.SftpPassword),
            SftpRemotePath = settings.SftpRemotePath,
            TunnelPublicUrl = settings.TunnelPublicUrl,
            ExposureMode = settings.ExposureMode,
            ManualTunnelUrl = settings.ManualTunnelUrl,
            ExposureProvider = settings.ExposureProvider,
            ApiCapabilitiesJson = settings.ApiCapabilitiesJson,
            ApiFeatureCatalogJson = settings.ApiFeatureCatalogJson,
            EnabledFeatureCodesJson = settings.EnabledFeatureCodesJson,
            SelectedFoldersJson = settings.SelectedFoldersJson,
            FeatureFolderSelectionsJson = settings.FeatureFolderSelectionsJson,
            FeatureCatalogSnapshotsJson = settings.FeatureCatalogSnapshotsJson
        };

        var json = JsonSerializer.Serialize(copy, JsonOptions);
        File.WriteAllText(_settingsPath, json, Encoding.UTF8);
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string TryUnprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            var bytes = Convert.FromBase64String(value);
            try
            {
                var unprotectedLm = ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(unprotectedLm);
            }
            catch
            {
                // Backward compatibility: some older builds may have used CurrentUser scope.
                var unprotectedCu = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(unprotectedCu);
            }
        }
        catch
        {
            return value; // Déjà en clair / invalide
        }
    }
}

