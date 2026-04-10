using System;

namespace Oxydriver.Services;

public sealed class AppSettings
{
    public string AppVersion { get; set; } = "0.1.0.0";
    public string ApiBaseUrl { get; set; } = "http://localhost:8080";

    // Identifiant local (clé d'accès) envoyée à la synchro initiale. À stocker de façon protégée.
    public string AccessKey { get; set; } = string.Empty;

    // Token retourné par l'API après validation/versioning.
    public string? ApiToken { get; set; }

    // Token "client final" à fournir au front/espace client (x-client-token).
    public string ClientToken { get; set; } = string.Empty;

    public string SqlConnectionString { get; set; } = string.Empty;
    public string SqlServerHost { get; set; } = "127.0.0.1";
    public string SqlAuthenticationMode { get; set; } = "SqlServer";
    public string SqlUserName { get; set; } = string.Empty;
    public string SqlPassword { get; set; } = string.Empty;
    public string SqlRuntimeUserName { get; set; } = "OXYDRIVER_APP";
    public string SqlRuntimePassword { get; set; } = string.Empty;
    public string SqlEncryptMode { get; set; } = "Mandatory";
    public bool SqlTrustServerCertificate { get; set; } = true;
    public string SqlConnectTimeoutSeconds { get; set; } = "15";
    public string DefaultDatabase { get; set; } = string.Empty;

    public string LocalPort { get; set; } = "5179";

    public bool LaunchAtStartup { get; set; } = false;

    // Debug SFTP (tests de connectivite / fallback download).
    public string SftpHost { get; set; } = string.Empty;
    public string SftpPort { get; set; } = "22";
    public string SftpUsername { get; set; } = string.Empty;
    public string SftpPassword { get; set; } = string.Empty;
    public string SftpRemotePath { get; set; } = string.Empty;

    // URL publique cloudflare connue (si récupérée).
    public string? TunnelPublicUrl { get; set; }
    public string ExposureMode { get; set; } = "CloudflareAuto";
    public string? ManualTunnelUrl { get; set; }
    public string ExposureProvider { get; set; } = "cloudflare";

    // Cache des capacités + droits (DB/tables/colonnes) au format JSON renvoyé par l'API.
    public string? ApiCapabilitiesJson { get; set; }
    public string? ApiFeatureCatalogJson { get; set; }
    public string? EnabledFeatureCodesJson { get; set; }
    public string? SelectedFoldersJson { get; set; }
    public string? FeatureFolderSelectionsJson { get; set; }
    public string? FeatureCatalogSnapshotsJson { get; set; }

    public int GetLocalPortOrDefault()
    {
        if (int.TryParse(LocalPort, out var p) && p is >= 1024 and <= 65535)
            return p;
        return 5179;
    }
}

