using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using Oxydriver.Services;
using Renci.SshNet;

namespace Oxydriver.Ui;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly AppSettingsStore _settingsStore;
    private readonly CloudflaredManager _cloudflared;
    private readonly OnlineApiClient _api;
    private readonly LocalGatewayServer _server;
    private readonly StartupIntegration _startupIntegration;
    private readonly SettingsBackupService _backupService;
    private readonly AppLogStore _appLogStore;
    private readonly DispatcherTimer _startupBlinkTimer;
    private readonly DispatcherTimer _settingsDirtyTimer;
    private readonly DispatcherTimer _saveFeedbackTimer;
    private bool _blinkLow;
    private string _savedSettingsSnapshot = string.Empty;
    private bool _isSettingsDirty;
    private string _saveSettingsFeedbackText = string.Empty;
    private Dictionary<string, string> _savedSettingsMap = new(StringComparer.OrdinalIgnoreCase);

    public AppSettings Settings { get; }
    public string VersionText { get; }

    private string _sqlTestStatus = "—";
    public string SqlTestStatus { get => _sqlTestStatus; set { _sqlTestStatus = value; OnPropertyChanged(); } }
    private string _sqlSecurityStatus = "Compte SQL dédié: non provisionné.";
    public string SqlSecurityStatus { get => _sqlSecurityStatus; set { _sqlSecurityStatus = value; OnPropertyChanged(); } }

    private string _tunnelStatus = "—";
    public string TunnelStatus { get => _tunnelStatus; set { _tunnelStatus = value; OnPropertyChanged(); } }

    private string _clientTokenStatus = "—";
    public string ClientTokenStatus { get => _clientTokenStatus; set { _clientTokenStatus = value; OnPropertyChanged(); } }

    private string _syncStatus = "—";
    public string SyncStatus { get => _syncStatus; set { _syncStatus = value; OnPropertyChanged(); } }

    private string _updateStatus = "—";
    public string UpdateStatus { get => _updateStatus; set { _updateStatus = value; OnPropertyChanged(); } }
    private bool _isCheckingUpdates;
    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        set
        {
            if (_isCheckingUpdates == value) return;
            _isCheckingUpdates = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateLoaderVisibility));
        }
    }
    public System.Windows.Visibility UpdateLoaderVisibility =>
        IsCheckingUpdates ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    private string _sftpStatus = "—";
    public string SftpStatus { get => _sftpStatus; set { _sftpStatus = value; OnPropertyChanged(); } }
    private string? _pendingUpdateDownloadUrl;
    private string? _pendingUpdateVersion;
    private SftpConnectionInfo? _pendingUpdateSftp;
    private string[] _pendingUpdateChangeLines = [];
    public string UpdateActionButtonText =>
        (!string.IsNullOrWhiteSpace(_pendingUpdateDownloadUrl) || _pendingUpdateSftp is not null)
            ? $"Télécharger la version {_pendingUpdateVersion ?? "disponible"}"
            : "Chercher mise à jour";
    public string SaveSettingsFeedbackText
    {
        get => _saveSettingsFeedbackText;
        set { _saveSettingsFeedbackText = value; OnPropertyChanged(); }
    }
    private string _backupStatus = "—";
    public string BackupStatus { get => _backupStatus; set { _backupStatus = value; OnPropertyChanged(); } }
    public string BackupPassword { get; set; } = string.Empty;
    private string _headerIndicatorText = "Initialisation";
    public string HeaderIndicatorText { get => _headerIndicatorText; set { _headerIndicatorText = value; OnPropertyChanged(); } }
    private System.Windows.Media.Brush _headerIndicatorBrush = System.Windows.Media.Brushes.Orange;
    public System.Windows.Media.Brush HeaderIndicatorBrush { get => _headerIndicatorBrush; set { _headerIndicatorBrush = value; OnPropertyChanged(); } }
    private double _headerIndicatorOpacity = 1.0;
    public double HeaderIndicatorOpacity { get => _headerIndicatorOpacity; set { _headerIndicatorOpacity = value; OnPropertyChanged(); } }

    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<UtilityLogLine> UtilityLogs { get; } = [];
    public ObservableCollection<ClientLogLine> ClientRequestLogs { get; } = [];
    public ObservableCollection<SqlDebugRoleLine> SqlDebugRoles { get; } = [];
    public ObservableCollection<SqlDebugPermissionLine> SqlDebugPermissions { get; } = [];
    public ICollectionView SqlDebugRolesView { get; }
    public ICollectionView SqlDebugPermissionsView { get; }
    public ObservableCollection<string> SqlDebugDatabaseOptions { get; } = ["(Toutes)"];
    public ObservableCollection<string> SqlDebugFeatureOptions { get; } = ["(Toutes)"];
    public ObservableCollection<string> SqlDebugPermissionOptions { get; } = ["(Toutes)"];
    public ObservableCollection<string> SqlDebugStateOptions { get; } = ["(Tous)"];
    public ObservableCollection<string> FeatureItems { get; } = [];
    public ObservableCollection<FeatureToggleItem> FeatureToggles { get; } = [];
    public ObservableCollection<FolderEntry> SelectedFolders { get; } = [];
    public ObservableCollection<FeatureCatalogChangeLine> FeatureCatalogChanges { get; } = [];
    public int ActiveFeatureCount => FeatureToggles.Count(x => x.IsEnabled);
    public bool HasFeatureCatalogChanges => FeatureCatalogChanges.Count > 0;
    public int FeatureCatalogChangesCount => FeatureCatalogChanges.Count;
    public string FeatureCatalogChangesSummary =>
        FeatureCatalogChangesCount == 0
            ? "Aucune evolution recente."
            : $"{FeatureCatalogChangesCount} changement(s) detecte(s).";
    private bool _isFeatureCatalogChangesExpanded;
    public bool IsFeatureCatalogChangesExpanded
    {
        get => _isFeatureCatalogChangesExpanded;
        set { _isFeatureCatalogChangesExpanded = value; OnPropertyChanged(); }
    }
    private string _featureCatalogAlertText = "Aucune évolution récente.";
    public string FeatureCatalogAlertText
    {
        get => _featureCatalogAlertText;
        set { _featureCatalogAlertText = value; OnPropertyChanged(); }
    }

    public ICommand TestSqlCommand { get; }
    public ICommand BuildSqlConnectionStringCommand { get; }
    public ICommand StartTunnelCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand ApplyStartupCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand ToggleFeatureCommand { get; }
    public ICommand TestSftpCommand { get; }
    public ICommand CopyClientTokenCommand { get; }
    public ICommand GenerateNewClientTokenCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand RemoveFolderItemCommand { get; }
    public ICommand OpenFeatureSiteCommand { get; }
    public ICommand ConfigureFeatureFoldersCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand OpenLogHistoryCommand { get; }
    public ICommand RefreshSqlDebugCommand { get; }

    private string _sqlDebugStatus = "Cliquez sur refresh pour charger l'audit SQL.";
    public string SqlDebugStatus { get => _sqlDebugStatus; set { _sqlDebugStatus = value; OnPropertyChanged(); } }
    private string _sqlDebugSelectedDatabase = "(Toutes)";
    public string SqlDebugSelectedDatabase
    {
        get => _sqlDebugSelectedDatabase;
        set { _sqlDebugSelectedDatabase = string.IsNullOrWhiteSpace(value) ? "(Toutes)" : value; OnPropertyChanged(); ApplySqlDebugFilters(); }
    }
    private string _sqlDebugSelectedFeature = "(Toutes)";
    public string SqlDebugSelectedFeature
    {
        get => _sqlDebugSelectedFeature;
        set { _sqlDebugSelectedFeature = string.IsNullOrWhiteSpace(value) ? "(Toutes)" : value; OnPropertyChanged(); ApplySqlDebugFilters(); }
    }
    private string _sqlDebugSelectedPermission = "(Toutes)";
    public string SqlDebugSelectedPermission
    {
        get => _sqlDebugSelectedPermission;
        set { _sqlDebugSelectedPermission = string.IsNullOrWhiteSpace(value) ? "(Toutes)" : value; OnPropertyChanged(); ApplySqlDebugFilters(); }
    }
    private string _sqlDebugSelectedState = "(Tous)";
    public string SqlDebugSelectedState
    {
        get => _sqlDebugSelectedState;
        set { _sqlDebugSelectedState = string.IsNullOrWhiteSpace(value) ? "(Tous)" : value; OnPropertyChanged(); ApplySqlDebugFilters(); }
    }
    private string _sqlDebugTableFilter = string.Empty;
    public string SqlDebugTableFilter
    {
        get => _sqlDebugTableFilter;
        set { _sqlDebugTableFilter = value ?? string.Empty; OnPropertyChanged(); ApplySqlDebugFilters(); }
    }
    private string _sqlDebugColumnFilter = string.Empty;
    public string SqlDebugColumnFilter
    {
        get => _sqlDebugColumnFilter;
        set { _sqlDebugColumnFilter = value ?? string.Empty; OnPropertyChanged(); ApplySqlDebugFilters(); }
    }

    public bool CanAddFolder => SelectedFolders.Count == 0 || SelectedFolders.All(f => !string.IsNullOrWhiteSpace(f.Name));
    public bool CanRemoveFolder => SelectedFolders.Count > 1;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string[] SqlAuthenticationModes { get; } = ["SqlServer", "Windows"];
    public string[] SqlEncryptModes { get; } = ["Optional", "Mandatory", "Strict"];
    public string[] ExposureModes { get; } = ["CloudflareAuto", "ManualUrl"];

    public MainWindowViewModel(
        AppSettingsStore settingsStore,
        CloudflaredManager cloudflared,
        OnlineApiClient api,
        LocalGatewayServer server,
        StartupIntegration startupIntegration,
        SettingsBackupService backupService,
        Assembly assembly
    )
    {
        _settingsStore = settingsStore;
        _cloudflared = cloudflared;
        _api = api;
        _server = server;
        _startupIntegration = startupIntegration;
        _backupService = backupService;
        _appLogStore = new AppLogStore();
        SqlDebugRolesView = CollectionViewSource.GetDefaultView(SqlDebugRoles);
        SqlDebugPermissionsView = CollectionViewSource.GetDefaultView(SqlDebugPermissions);
        SqlDebugRolesView.Filter = o => MatchSqlDebugRole(o as SqlDebugRoleLine);
        SqlDebugPermissionsView.Filter = o => MatchSqlDebugPermission(o as SqlDebugPermissionLine);

        Settings = _settingsStore.Load();
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "0.1.0.0";
        VersionText = $"v{assemblyVersion}";
        if (!string.Equals(Settings.AppVersion, assemblyVersion, StringComparison.OrdinalIgnoreCase))
        {
            Settings.AppVersion = assemblyVersion;
            _settingsStore.Save(Settings);
        }

        TestSqlCommand = new AsyncRelayCommand(TestSqlAsync);
        BuildSqlConnectionStringCommand = new AsyncRelayCommand(BuildSqlConnectionStringAsync);
        StartTunnelCommand = new AsyncRelayCommand(StartTunnelAsync);
        SyncCommand = new AsyncRelayCommand(SyncAsync);
        ApplyStartupCommand = new AsyncRelayCommand(ApplyStartupAsync);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
        ClearLogsCommand = new AsyncRelayCommand(ClearLogsAsync);
        ToggleFeatureCommand = new AsyncRelayCommand(ToggleFeatureAsync);
        TestSftpCommand = new AsyncRelayCommand(TestSftpAsync);
        CopyClientTokenCommand = new AsyncRelayCommand(CopyClientTokenAsync);
        GenerateNewClientTokenCommand = new AsyncRelayCommand(GenerateNewClientTokenAsync);
        AddFolderCommand = new RelayCommand(_ => AddFolder(), _ => CanAddFolder);
        RemoveFolderCommand = new RelayCommand(_ => RemoveLastFolder(), _ => CanRemoveFolder);
        RemoveFolderItemCommand = new RelayCommand(p => RemoveFolderItem(p as FolderEntry), _ => CanRemoveFolder);
        OpenFeatureSiteCommand = new RelayCommand(p => OpenFeatureSite(p as FeatureToggleItem));
        ConfigureFeatureFoldersCommand = new RelayCommand(p => ConfigureFeatureFolders(p as FeatureToggleItem));
        SaveSettingsCommand = new AsyncRelayCommand(() => SaveSettingsAsync());
        OpenLogHistoryCommand = new RelayCommand(_ => OpenLogHistory());
        RefreshSqlDebugCommand = new AsyncRelayCommand(RefreshSqlDebugAsync);
        FeatureCatalogChanges.CollectionChanged += OnFeatureCatalogChangesCollectionChanged;
        _startupBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _startupBlinkTimer.Tick += (_, _) =>
        {
            _blinkLow = !_blinkLow;
            HeaderIndicatorOpacity = _blinkLow ? 0.35 : 1.0;
        };
        _savedSettingsMap = BuildSettingsMap();
        _savedSettingsSnapshot = BuildSettingsSnapshot(_savedSettingsMap);
        _settingsDirtyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _settingsDirtyTimer.Tick += (_, _) => RefreshSettingsDirtyState();
        _settingsDirtyTimer.Start();
        _saveFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _saveFeedbackTimer.Tick += (_, _) =>
        {
            _saveFeedbackTimer.Stop();
            SaveSettingsFeedbackText = string.Empty;
        };
        SyncFoldersFromSettings();
        SelectedFolders.CollectionChanged += OnFoldersCollectionChanged;
        RefreshFeaturesFromSettings();
        _server.ClientRequestLogged += OnClientRequestLogged;
        LogUtility("Application démarrée.");
        _ = AutoStartupSequenceAsync();
    }

    private void OnFeatureCatalogChangesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasFeatureCatalogChanges));
        OnPropertyChanged(nameof(FeatureCatalogChangesCount));
        OnPropertyChanged(nameof(FeatureCatalogChangesSummary));
    }

    private async Task TestSqlAsync()
    {
        await TestSqlCoreAsync();
        RefreshHeaderStateFromCurrentStatuses();
    }

    private async Task<bool> TestSqlCoreAsync()
    {
        if (RequireSavedSettings("test SQL"))
            return false;
        SqlTestStatus = "Test en cours…";
        try
        {
            var (provisioned, _) = await EnsureSqlRuntimeAccountAsync();
            if (!provisioned)
                return false;
            BuildRuntimeSqlConnectionString();
            var cs = Settings.SqlConnectionString ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cs))
            {
                SqlTestStatus = "Chaîne de connexion manquante.";
                return false;
            }

            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            var val = await cmd.ExecuteScalarAsync();
            SqlTestStatus = val?.ToString() == "1" ? "OK" : "Connecté (réponse inattendue).";
            LogUtility("Test SQL réussi.");
            return true;
        }
        catch (Exception ex)
        {
            SqlTestStatus = $"Erreur: {ex.Message}";
            LogUtility($"Erreur test SQL: {ex.Message}");
            return false;
        }
    }

    private Task BuildSqlConnectionStringAsync()
    {
        try
        {
            BuildSqlAdminConnectionString();
            SqlTestStatus = "Chaîne de connexion générée.";
            LogUtility("Chaîne SQL générée.");
        }
        catch (Exception ex)
        {
            SqlTestStatus = $"Erreur: {ex.Message}";
            LogUtility($"Erreur génération chaîne SQL: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private void BuildSqlAdminConnectionString()
    {
        var dataSource = (Settings.SqlServerHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("Nom du serveur SQL manquant.");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = (Settings.DefaultDatabase ?? string.Empty).Trim(),
            Encrypt = ParseEncryptMode(Settings.SqlEncryptMode),
            TrustServerCertificate = Settings.SqlTrustServerCertificate,
            ConnectTimeout = ParseTimeout(Settings.SqlConnectTimeoutSeconds),
            ApplicationName = "OXYDRIVER"
        };

        var windowsAuth = string.Equals(Settings.SqlAuthenticationMode, "Windows", StringComparison.OrdinalIgnoreCase);
        builder.IntegratedSecurity = windowsAuth;
        if (!windowsAuth)
        {
            builder.UserID = Settings.SqlUserName ?? string.Empty;
            builder.Password = Settings.SqlPassword ?? string.Empty;
        }

        Settings.SqlConnectionString = builder.ConnectionString;
        _settingsStore.Save(Settings);
    }

    private void BuildRuntimeSqlConnectionString()
    {
        var dataSource = (Settings.SqlServerHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("Nom du serveur SQL manquant.");
        var runtimeUser = (Settings.SqlRuntimeUserName ?? string.Empty).Trim();
        var runtimePassword = Settings.SqlRuntimePassword ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runtimeUser) || string.IsNullOrWhiteSpace(runtimePassword))
            throw new InvalidOperationException("Compte SQL OXYDRIVER non provisionné.");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = (Settings.DefaultDatabase ?? string.Empty).Trim(),
            Encrypt = ParseEncryptMode(Settings.SqlEncryptMode),
            TrustServerCertificate = Settings.SqlTrustServerCertificate,
            ConnectTimeout = ParseTimeout(Settings.SqlConnectTimeoutSeconds),
            ApplicationName = "OXYDRIVER",
            IntegratedSecurity = false,
            UserID = runtimeUser,
            Password = runtimePassword
        };
        Settings.SqlConnectionString = builder.ConnectionString;
        _settingsStore.Save(Settings);
    }

    private static bool ParseEncryptMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "optional" => false,
            _ => true
        };

    private static int ParseTimeout(string? raw)
    {
        if (int.TryParse(raw, out var t) && t >= 0 && t <= 300)
            return t;
        return 15;
    }

    /// <summary>
    /// Provisionne le login SQL runtime et l'utilisateur dans chaque base listée.
    /// Les bases absentes de l'instance sont ignorées (non bloquant).
    /// </summary>
    /// <returns>Succès et la liste des bases où le user a été (ou était déjà) assuré.</returns>
    private async Task<(bool Success, string[] DatabasesReady)> EnsureSqlRuntimeAccountAsync(IEnumerable<string>? targetDatabases = null)
    {
        var sqlAuth = string.Equals(Settings.SqlAuthenticationMode, "SqlServer", StringComparison.OrdinalIgnoreCase);
        if (!sqlAuth)
        {
            SqlTestStatus = "Provisionnement compte OXYDRIVER requis en mode SqlServer.";
            LogUtility(SqlTestStatus);
            return (false, []);
        }
        if (string.IsNullOrWhiteSpace(Settings.SqlUserName) || string.IsNullOrWhiteSpace(Settings.SqlPassword))
        {
            SqlTestStatus = "Compte admin SQL requis pour provisionner OXYDRIVER.";
            LogUtility(SqlTestStatus);
            return (false, []);
        }

        var runtimeUser = string.IsNullOrWhiteSpace(Settings.SqlRuntimeUserName)
            ? "OXYDRIVER_APP"
            : Settings.SqlRuntimeUserName.Trim();
        var runtimePasswordWasEmpty = string.IsNullOrWhiteSpace(Settings.SqlRuntimePassword);
        var runtimePassword = runtimePasswordWasEmpty ? GenerateStrongSqlPassword() : Settings.SqlRuntimePassword;

        var adminBuilder = new SqlConnectionStringBuilder
        {
            DataSource = (Settings.SqlServerHost ?? string.Empty).Trim(),
            InitialCatalog = "master",
            Encrypt = ParseEncryptMode(Settings.SqlEncryptMode),
            TrustServerCertificate = Settings.SqlTrustServerCertificate,
            ConnectTimeout = ParseTimeout(Settings.SqlConnectTimeoutSeconds),
            ApplicationName = "OXYDRIVER-Admin",
            IntegratedSecurity = false,
            UserID = Settings.SqlUserName,
            Password = Settings.SqlPassword
        };

        var dbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Settings.DefaultDatabase))
            dbs.Add(Settings.DefaultDatabase.Trim());
        if (targetDatabases is not null)
        {
            foreach (var db in targetDatabases.Select(x => (x ?? string.Empty).Trim()).Where(x => !string.IsNullOrWhiteSpace(x)))
                dbs.Add(db);
        }

        try
        {
            await using var conn = new SqlConnection(adminBuilder.ConnectionString);
            await conn.OpenAsync();
            var existingOnServer = await SqlServerCatalog.ListDatabaseNamesAsync(conn);
            foreach (var db in dbs.ToArray())
            {
                if (existingOnServer.Contains(db)) continue;
                dbs.Remove(db);
                LogUtility($"Base SQL absente sur l'instance, ignorée: {db}");
            }

            await using (var existsCmd = conn.CreateCommand())
            {
                existsCmd.CommandText = "SELECT COUNT(1) FROM sys.sql_logins WHERE name = @name";
                existsCmd.Parameters.AddWithValue("@name", runtimeUser);
                var count = Convert.ToInt32(await existsCmd.ExecuteScalarAsync());
                if (count == 0)
                {
                    await using var createCmd = conn.CreateCommand();
                    createCmd.CommandText = $"CREATE LOGIN {SqlIdent(runtimeUser)} WITH PASSWORD = {SqlLiteral(runtimePassword)}, CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;";
                    await createCmd.ExecuteNonQueryAsync();
                    LogUtility($"Utilisateur SQL '{runtimeUser}' créé.");
                    SqlSecurityStatus = $"Compte SQL dédié créé: {runtimeUser}";
                }
                else if (runtimePasswordWasEmpty)
                {
                    await using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = $"ALTER LOGIN {SqlIdent(runtimeUser)} WITH PASSWORD = {SqlLiteral(runtimePassword)};";
                    await alterCmd.ExecuteNonQueryAsync();
                    LogUtility($"Mot de passe SQL régénéré pour '{runtimeUser}'.");
                    SqlSecurityStatus = $"Compte SQL dédié actif: {runtimeUser} (mot de passe régénéré)";
                }
                else
                {
                    SqlSecurityStatus = $"Compte SQL dédié actif: {runtimeUser}";
                }
            }

            foreach (var db in dbs)
            {
                conn.ChangeDatabase(db);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    $"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {SqlLiteral(runtimeUser)}) " +
                    $"CREATE USER {SqlIdent(runtimeUser)} FOR LOGIN {SqlIdent(runtimeUser)};";
                await cmd.ExecuteNonQueryAsync();
            }
            conn.ChangeDatabase("master");
        }
        catch (Exception ex)
        {
            SqlTestStatus = $"Erreur provisionnement SQL OXYDRIVER: {ex.Message}";
            SqlSecurityStatus = SqlTestStatus;
            LogUtility(SqlTestStatus);
            return (false, []);
        }

        Settings.SqlRuntimeUserName = runtimeUser;
        Settings.SqlRuntimePassword = runtimePassword;
        _settingsStore.Save(Settings);
        OnPropertyChanged(nameof(Settings));
        var ready = dbs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return (true, ready);
    }

    private static string GenerateStrongSqlPassword()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        var base64 = Convert.ToBase64String(bytes).Replace("/", "A").Replace("+", "B").TrimEnd('=');
        return $"Oxy!{base64}9z";
    }

    private static HashSet<string> ParseEnabledFeatureCodes(string? raw)
    {
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(raw ?? "[]") ?? [];
            return values
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string BuildSqlFeatureRoleName(string? featureCode)
    {
        var source = (featureCode ?? string.Empty).Trim().ToUpperInvariant();
        var clean = new string(source
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');
        if (string.IsNullOrWhiteSpace(clean)) clean = "FEATURE";
        return $"OXYDRIVER_FEAT_{clean}";
    }

    private static string SqlIdent(string name) => $"[{(name ?? string.Empty).Replace("]", "]]")}]";
    private static string SqlLiteral(string value) => $"N'{(value ?? string.Empty).Replace("'", "''")}'";

    private async Task StartTunnelAsync()
    {
        await StartTunnelCoreAsync();
        RefreshHeaderStateFromCurrentStatuses();
    }

    private async Task<bool> StartTunnelCoreAsync()
    {
        if (RequireSavedSettings("démarrage tunnel"))
            return false;
        TunnelStatus = "Préparation…";
        try
        {
            _settingsStore.Save(Settings);
            _server.UpdateAuthorizationPolicy(Settings);

            var port = Settings.GetLocalPortOrDefault();
            await _server.EnsureStartedAsync(port);

            if (string.Equals(Settings.ExposureMode, "ManualUrl", StringComparison.OrdinalIgnoreCase))
            {
                var manualUrl = (Settings.ManualTunnelUrl ?? string.Empty).Trim();
                if (!Uri.TryCreate(manualUrl, UriKind.Absolute, out _))
                {
                    TunnelStatus = "Erreur: URL tunnel manuelle invalide.";
                    LogUtility(TunnelStatus);
                    return false;
                }
                Settings.TunnelPublicUrl = manualUrl;
                Settings.ExposureProvider = string.IsNullOrWhiteSpace(Settings.ExposureProvider) ? "manual" : Settings.ExposureProvider.Trim().ToLowerInvariant();
                _settingsStore.Save(Settings);
                OnPropertyChanged(nameof(Settings));
                TunnelStatus = $"Tunnel manuel: {manualUrl}";
                LogUtility(TunnelStatus);
                await TryAutoSyncTunnelMappingAsync();
                return true;
            }

            // On click, force a restart to refresh the public URL (quick tunnels can change).
            var result = await _cloudflared.EnsureInstalledAndStartAsync(port, forceRestart: true);
            TunnelStatus = result.IsSuccess ? $"Tunnel: {result.StatusText}" : $"Erreur: {result.StatusText}";
            if (!string.IsNullOrWhiteSpace(result.PublicUrl))
            {
                Settings.TunnelPublicUrl = result.PublicUrl;
                Settings.ExposureProvider = "cloudflare";
                _settingsStore.Save(Settings);
                OnPropertyChanged(nameof(Settings));
            }
            LogUtility(TunnelStatus);
            await TryAutoSyncTunnelMappingAsync();
            return true;
        }
        catch (Exception ex)
        {
            TunnelStatus = $"Erreur: {ex.Message}";
            LogUtility(TunnelStatus);
            return false;
        }
    }

    private async Task SyncAsync()
    {
        await SyncCoreAsync();
        RefreshHeaderStateFromCurrentStatuses();
    }

    private async Task<bool> SyncCoreAsync()
    {
        if (RequireSavedSettings("synchronisation API"))
            return false;
        SyncStatus = "Sync en cours…";
        try
        {
            Settings.AccessKey = (Settings.AccessKey ?? string.Empty).Trim();
            _settingsStore.Save(Settings);
            var oldCatalogRaw = Settings.ApiFeatureCatalogJson ?? string.Empty;

            var sync = await _api.SyncAsync(Settings);
            if (!sync.IsSuccess)
            {
                if (sync.Message.Contains("invalid_or_revoked_access_key", StringComparison.OrdinalIgnoreCase))
                {
                    SyncStatus = "Refusé: clé d'accès invalide/révoquée. Vérifie la clé API dans Paramétrage.";
                    LogUtility(SyncStatus);
                    return false;
                }
                SyncStatus = $"Refusé: {sync.Message}";
                LogUtility(SyncStatus);
                return false;
            }

            Settings.ApiToken = sync.ApiToken ?? Settings.ApiToken;
            Settings.ApiCapabilitiesJson = sync.CapabilitiesJson ?? Settings.ApiCapabilitiesJson;
            if (!string.IsNullOrWhiteSpace(sync.UiPassword))
            {
                Settings.UiPassword = sync.UiPassword!;
                Settings.UiPasswordMustChange = true;
                LogUtility("Un mot de passe interface temporaire a ete recu via la synchronisation API.");
            }
            var effectiveFolders = sync.TokenFolders.Length > 0 ? sync.TokenFolders : sync.SelectedFolders;
            if (ApplyFoldersFromApiSync(effectiveFolders))
                LogUtility($"Dossiers client récupérés depuis la clé: {string.Join(", ", effectiveFolders)}");
            if (sync.HasUpdate)
            {
                // IMPORTANT:
                // Tant que la nouvelle version n'est pas téléchargée/validée, on n'applique pas
                // de nouveau catalogue d'exposition pour éviter toute ouverture anticipée.
                Settings.ApiFeatureCatalogJson = oldCatalogRaw;
                _settingsStore.Save(Settings);
                _pendingUpdateVersion = sync.LatestVersion;
                _pendingUpdateDownloadUrl = string.IsNullOrWhiteSpace(sync.DownloadUrl) ? null : sync.DownloadUrl;
                _pendingUpdateSftp = sync.Sftp;
                OnPropertyChanged(nameof(UpdateActionButtonText));
                if (!string.IsNullOrWhiteSpace(_pendingUpdateVersion))
                    LogUtility($"Mise à jour détectée pendant la synchronisation: version {_pendingUpdateVersion}.");
            }
            else
            {
                Settings.ApiFeatureCatalogJson = sync.FeatureCatalogJson ?? Settings.ApiFeatureCatalogJson;
                var sqlRightsOk = await ApplySqlPermissionsForRuntimeUserAsync(oldCatalogRaw, Settings.ApiFeatureCatalogJson ?? string.Empty);
                if (!sqlRightsOk)
                {
                    Settings.ApiFeatureCatalogJson = oldCatalogRaw;
                    _settingsStore.Save(Settings);
                    SyncStatus = "Refusé: droits SQL non appliqués (catalogue conservé).";
                    LogUtility(SyncStatus);
                    return false;
                }
                _settingsStore.Save(Settings);
                SaveFeatureCatalogSnapshot(Settings.AppVersion, Settings.ApiFeatureCatalogJson);
                UpdateFeatureCatalogChanges(oldCatalogRaw, Settings.ApiFeatureCatalogJson ?? string.Empty);
                RefreshFeaturesFromSettings();
                _server.UpdateAuthorizationPolicy(Settings);
                if (!string.IsNullOrWhiteSpace(_pendingUpdateDownloadUrl) || _pendingUpdateSftp is not null)
                    ResetPendingUpdate();
            }

            await EnsureClientTokenBoundAsync();

            SyncStatus = string.IsNullOrWhiteSpace(sync.ApiVersion)
                ? "OK (droits mis à jour)."
                : $"OK (API {sync.ApiVersion}, droits mis à jour).";
            LogUtility(SyncStatus);
            return true;
        }
        catch (HttpRequestException ex)
        {
            SyncStatus = $"API injoignable: {ex.Message}";
            LogUtility(SyncStatus);
            return false;
        }
        catch (Exception ex)
        {
            SyncStatus = $"Erreur: {ex.Message}";
            LogUtility(SyncStatus);
            return false;
        }
    }

    private bool ApplyFoldersFromApiSync(IEnumerable<string>? folders)
    {
        var normalized = (folders ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0) return false;

        var current = SelectedFolders
            .Select(x => (x.Name ?? string.Empty).Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (current.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
            return false;

        Settings.SelectedFoldersJson = JsonSerializer.Serialize(normalized);
        _settingsStore.Save(Settings);
        SyncFoldersFromSettings();
        return true;
    }

    private async Task<bool> ApplySqlPermissionsForRuntimeUserAsync(string oldCatalogRaw, string newCatalogRaw)
    {
        var features = JsonSerializer.Deserialize<FeatureDefinition[]>(
            newCatalogRaw ?? "[]",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var enabledFeatures = ParseEnabledFeatureCodes(Settings.EnabledFeatureCodesJson);
        var allDbs = features
            .SelectMany(f => f.Resources)
            .Select(r => (r.Database ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var (provisioned, dbsForRights) = await EnsureSqlRuntimeAccountAsync(allDbs);
        if (!provisioned) return false;

        var adminBuilder = new SqlConnectionStringBuilder
        {
            DataSource = (Settings.SqlServerHost ?? string.Empty).Trim(),
            InitialCatalog = "master",
            Encrypt = ParseEncryptMode(Settings.SqlEncryptMode),
            TrustServerCertificate = Settings.SqlTrustServerCertificate,
            ConnectTimeout = ParseTimeout(Settings.SqlConnectTimeoutSeconds),
            ApplicationName = "OXYDRIVER-Admin-Rights",
            IntegratedSecurity = false,
            UserID = Settings.SqlUserName,
            Password = Settings.SqlPassword
        };
        var runtimeUser = (Settings.SqlRuntimeUserName ?? string.Empty).Trim();
        try
        {
            await using var conn = new SqlConnection(adminBuilder.ConnectionString);
            await conn.OpenAsync();
            foreach (var db in dbsForRights)
            {
                conn.ChangeDatabase(db);

                // Toujours retirer le user de tous les rôles fonctionnalités, puis réaffecter les activés.
                var existingRoles = new List<string>();
                await using (var listRoles = conn.CreateCommand())
                {
                    listRoles.CommandText = @"
SELECT r.name
FROM sys.database_role_members drm
JOIN sys.database_principals r ON drm.role_principal_id = r.principal_id
JOIN sys.database_principals u ON drm.member_principal_id = u.principal_id
WHERE u.name = @user AND r.name LIKE 'OXYDRIVER_FEAT_%'";
                    listRoles.Parameters.AddWithValue("@user", runtimeUser);
                    await using var reader = await listRoles.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        existingRoles.Add(reader.GetString(0));
                }
                foreach (var role in existingRoles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    await using var dropCmd = conn.CreateCommand();
                    dropCmd.CommandText = $"ALTER ROLE {SqlIdent(role)} DROP MEMBER {SqlIdent(runtimeUser)};";
                    await dropCmd.ExecuteNonQueryAsync();
                }

                var featuresForDb = features
                    .Select(f => new
                    {
                        FeatureCode = (f.Code ?? string.Empty).Trim(),
                        RoleName = BuildSqlFeatureRoleName(f.Code),
                        IsEnabled = enabledFeatures.Contains((f.Code ?? string.Empty).Trim()),
                        Resources = f.Resources
                            .Where(r => string.Equals((r.Database ?? string.Empty).Trim(), db, StringComparison.OrdinalIgnoreCase))
                            .ToArray()
                    })
                    .Where(x => x.Resources.Length > 0 && !string.IsNullOrWhiteSpace(x.FeatureCode))
                    .ToArray();

                foreach (var feature in featuresForDb)
                {
                    await using (var createRoleCmd = conn.CreateCommand())
                    {
                        createRoleCmd.CommandText =
                            $"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE type = 'R' AND name = {SqlLiteral(feature.RoleName)}) " +
                            $"CREATE ROLE {SqlIdent(feature.RoleName)};";
                        await createRoleCmd.ExecuteNonQueryAsync();
                    }

                    var tablePlans = feature.Resources
                        .GroupBy(r => (r.Table ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                        .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                        .Select(g =>
                        {
                            var readAll = false;
                            var readCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var writeCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var res in g)
                            {
                                foreach (var col in res.Columns)
                                {
                                    if (col.Rights.Any(x => string.Equals(x, "read", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        if (string.Equals(col.Name, "*", StringComparison.OrdinalIgnoreCase)) readAll = true;
                                        else readCols.Add(col.Name);
                                    }
                                    if (col.Rights.Any(x => string.Equals(x, "write", StringComparison.OrdinalIgnoreCase)))
                                        writeCols.Add(col.Name);
                                }
                            }
                            return new
                            {
                                Table = g.Key,
                                ReadAll = readAll,
                                ReadCols = readCols.ToArray(),
                                WriteCols = writeCols.ToArray()
                            };
                        })
                        .ToArray();

                    foreach (var t in tablePlans)
                    {
                        var fqTable = $"{SqlIdent("dbo")}.{SqlIdent(t.Table)}";
                        var grantSelectRole = t.ReadAll
                            ? $"GRANT SELECT ON {fqTable} TO {SqlIdent(feature.RoleName)};"
                            : (t.ReadCols.Length > 0
                                ? $"GRANT SELECT ({string.Join(", ", t.ReadCols.Select(SqlIdent))}) ON {fqTable} TO {SqlIdent(feature.RoleName)};"
                                : string.Empty);
                        var grantUpdateRole = t.WriteCols.Length > 0
                            ? $"GRANT UPDATE ({string.Join(", ", t.WriteCols.Select(SqlIdent))}) ON {fqTable} TO {SqlIdent(feature.RoleName)};"
                            : string.Empty;

                        await using var permCmd = conn.CreateCommand();
                        permCmd.CommandText =
                            $"IF OBJECT_ID({SqlLiteral($"dbo.{t.Table}")}, 'U') IS NOT NULL BEGIN " +
                            // Nettoyage ancien mode: droits directs sur le user runtime.
                            $"REVOKE SELECT ON {fqTable} FROM {SqlIdent(runtimeUser)}; " +
                            $"REVOKE UPDATE ON {fqTable} FROM {SqlIdent(runtimeUser)}; " +
                            // Rebase du rôle.
                            $"REVOKE SELECT ON {fqTable} FROM {SqlIdent(feature.RoleName)}; " +
                            $"REVOKE UPDATE ON {fqTable} FROM {SqlIdent(feature.RoleName)}; " +
                            grantSelectRole + grantUpdateRole +
                            $" END";
                        await permCmd.ExecuteNonQueryAsync();
                        LogUtility($"Rôle SQL '{feature.RoleName}' sur '{db}.{t.Table}': SELECT[{(t.ReadAll ? "*" : t.ReadCols.Length)}], UPDATE[{t.WriteCols.Length}].");
                    }

                    if (feature.IsEnabled)
                    {
                        await using var addMemberCmd = conn.CreateCommand();
                        addMemberCmd.CommandText =
                            $"IF NOT EXISTS (" +
                            $"SELECT 1 FROM sys.database_role_members drm " +
                            $"JOIN sys.database_principals r ON drm.role_principal_id = r.principal_id " +
                            $"JOIN sys.database_principals u ON drm.member_principal_id = u.principal_id " +
                            $"WHERE r.name = {SqlLiteral(feature.RoleName)} AND u.name = {SqlLiteral(runtimeUser)}) " +
                            $"ALTER ROLE {SqlIdent(feature.RoleName)} ADD MEMBER {SqlIdent(runtimeUser)};";
                        await addMemberCmd.ExecuteNonQueryAsync();
                        LogUtility($"Feature '{feature.FeatureCode}': rôle SQL activé pour '{runtimeUser}' dans {db}.");
                    }
                    else
                    {
                        LogUtility($"Feature '{feature.FeatureCode}': rôle SQL désactivé pour '{runtimeUser}' dans {db}.");
                    }
                }
            }
            conn.ChangeDatabase("master");
            SqlSecurityStatus = $"Droits SQL appliqués via rôles pour le compte '{runtimeUser}'.";
            LogUtility(SqlSecurityStatus);
            BuildRuntimeSqlConnectionString();
            return true;
        }
        catch (Exception ex)
        {
            SqlSecurityStatus = $"Erreur droits SQL: {ex.Message}";
            LogUtility($"Erreur application droits SQL: {ex.Message}");
            return false;
        }
    }

    private static string GenerateClientToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"cli_live_{token}";
    }

    private async Task EnsureClientTokenBoundAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Settings.ApiBaseUrl) || string.IsNullOrWhiteSpace(Settings.AccessKey))
            {
                ClientTokenStatus = "ClientToken: config API manquante.";
                return;
            }

            if (string.IsNullOrWhiteSpace(Settings.ClientToken))
            {
                Settings.ClientToken = GenerateClientToken();
                _settingsStore.Save(Settings);
                OnPropertyChanged(nameof(Settings));
            }

            ClientTokenStatus = "ClientToken: génération…";
            var bind = await _api.BindClientTokenAsync(Settings.ApiBaseUrl, Settings.AccessKey, Settings.ClientToken);
            if (!bind.IsSuccess)
            {
                ClientTokenStatus = $"ClientToken: erreur ({bind.Message})";
                LogUtility(ClientTokenStatus);
                return;
            }

            ClientTokenStatus = "ClientToken: OK";
            LogUtility($"ClientToken prêt (à fournir au front): {Settings.ClientToken}");
        }
        catch (Exception ex)
        {
            ClientTokenStatus = $"ClientToken: erreur ({ex.Message})";
            LogUtility(ClientTokenStatus);
        }
    }

    private async Task GenerateNewClientTokenAsync()
    {
        if (RequireSavedSettings("token client"))
            return;
        try
        {
            if (string.IsNullOrWhiteSpace(Settings.ApiBaseUrl) || string.IsNullOrWhiteSpace(Settings.AccessKey))
            {
                ClientTokenStatus = "ClientToken: config API manquante.";
                LogUtility(ClientTokenStatus);
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                "Générer un nouveau TokenClient ?\n\nCela invalidera l'ancien token côté front (il faudra le remplacer).",
                "Nouveau TokenClient",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning
            );
            if (confirm != System.Windows.MessageBoxResult.Yes)
                return;

            var newToken = GenerateClientToken();
            ClientTokenStatus = "ClientToken: rotation…";
            var rotate = await _api.RotateClientTokenAsync(Settings.ApiBaseUrl, Settings.AccessKey, newToken);
            if (!rotate.IsSuccess)
            {
                ClientTokenStatus = $"ClientToken: erreur ({rotate.Message})";
                LogUtility(ClientTokenStatus);
                return;
            }

            Settings.ClientToken = newToken;
            _settingsStore.Save(Settings);
            OnPropertyChanged(nameof(Settings));

            ClientTokenStatus = "ClientToken: OK";
            LogUtility($"Nouveau ClientToken prêt (anciens supprimés: {rotate.DeletedCount}): {Settings.ClientToken}");
        }
        catch (Exception ex)
        {
            ClientTokenStatus = $"ClientToken: erreur ({ex.Message})";
            LogUtility(ClientTokenStatus);
        }
    }

    private Task CopyClientTokenAsync()
    {
        if (RequireSavedSettings("token client"))
            return Task.CompletedTask;
        try
        {
            var token = Settings.ClientToken ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                ClientTokenStatus = "ClientToken vide.";
                LogUtility(ClientTokenStatus);
                return Task.CompletedTask;
            }
            System.Windows.Clipboard.SetText(token);
            ClientTokenStatus = "ClientToken copié.";
            LogUtility(ClientTokenStatus);
        }
        catch (Exception ex)
        {
            ClientTokenStatus = $"Erreur copie: {ex.Message}";
            LogUtility(ClientTokenStatus);
        }
        return Task.CompletedTask;
    }

    private async Task ApplyStartupAsync()
    {
        if (RequireSavedSettings("démarrage auto"))
            return;
        try
        {
            await _startupIntegration.ApplyAsync(Settings.LaunchAtStartup);
            UpdateStatus = Settings.LaunchAtStartup ? "Démarrage auto activé." : "Démarrage auto désactivé.";
            LogUtility(UpdateStatus);
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Erreur: {ex.Message}";
            LogUtility(UpdateStatus);
        }
    }

    /// <summary>
    /// Point d'entrée utilisé par le formulaire Paramétrage :
    /// les valeurs du brouillon sont appliquées d'un coup au moment de l'enregistrement.
    /// </summary>
    public async Task SaveSettingsFromFormAsync(AppSettings draft)
    {
        var previousSettings = CloneSettings(Settings);
        ApplyEditableSettings(draft);
        await SaveSettingsAsync(previousSettings);
    }

    private async Task SaveSettingsAsync(AppSettings? previousSettings = null)
    {
        try
        {
            previousSettings ??= CloneSettings(Settings);
            var before = _savedSettingsMap;
            PersistFoldersToSettings();
            _settingsStore.Save(Settings);
            var after = BuildSettingsMap();
            LogSettingsChanges(before, after);
            _savedSettingsMap = after;
            _savedSettingsSnapshot = BuildSettingsSnapshot(_savedSettingsMap);
            RefreshSettingsDirtyState(force: true);
            SaveSettingsFeedbackText = "Paramétrage enregistré.";
            _saveFeedbackTimer.Stop();
            _saveFeedbackTimer.Start();
            LogUtility("Paramétrage enregistré. Relance automatique des contrôles (tunnel, sync API, SQL).");

            await _startupIntegration.ApplyAsync(Settings.LaunchAtStartup);
            LogUtility(Settings.LaunchAtStartup
                ? "Démarrage auto appliqué après enregistrement."
                : "Démarrage auto retiré après enregistrement.");

            // On ne relance que les connexions impactées par les champs modifiés.
            if (ShouldRefreshTunnel(previousSettings, Settings))
                await StartTunnelCoreAsync();
            if (ShouldRefreshApi(previousSettings, Settings))
                await SyncCoreAsync();
            if (ShouldRefreshSql(previousSettings, Settings))
                await TestSqlCoreAsync();
            RefreshHeaderStateFromCurrentStatuses();
        }
        catch (Exception ex)
        {
            LogUtility($"Erreur enregistrement paramétrage: {ex.Message}");
            SaveSettingsFeedbackText = "Erreur enregistrement.";
            _saveFeedbackTimer.Stop();
            _saveFeedbackTimer.Start();
        }
    }

    private static AppSettings CloneSettings(AppSettings source)
    {
        return new AppSettings
        {
            AppVersion = source.AppVersion,
            ApiBaseUrl = source.ApiBaseUrl,
            AccessKey = source.AccessKey,
            UiPassword = source.UiPassword,
            UiPasswordMustChange = source.UiPasswordMustChange,
            ApiToken = source.ApiToken,
            ClientToken = source.ClientToken,
            SqlConnectionString = source.SqlConnectionString,
            SqlServerHost = source.SqlServerHost,
            SqlAuthenticationMode = source.SqlAuthenticationMode,
            SqlUserName = source.SqlUserName,
            SqlPassword = source.SqlPassword,
            SqlRuntimeUserName = source.SqlRuntimeUserName,
            SqlRuntimePassword = source.SqlRuntimePassword,
            SqlEncryptMode = source.SqlEncryptMode,
            SqlTrustServerCertificate = source.SqlTrustServerCertificate,
            SqlConnectTimeoutSeconds = source.SqlConnectTimeoutSeconds,
            DefaultDatabase = source.DefaultDatabase,
            LocalPort = source.LocalPort,
            LaunchAtStartup = source.LaunchAtStartup,
            SftpHost = source.SftpHost,
            SftpPort = source.SftpPort,
            SftpUsername = source.SftpUsername,
            SftpPassword = source.SftpPassword,
            SftpRemotePath = source.SftpRemotePath,
            TunnelPublicUrl = source.TunnelPublicUrl,
            ExposureMode = source.ExposureMode,
            ManualTunnelUrl = source.ManualTunnelUrl,
            ExposureProvider = source.ExposureProvider,
            ApiCapabilitiesJson = source.ApiCapabilitiesJson,
            ApiFeatureCatalogJson = source.ApiFeatureCatalogJson,
            EnabledFeatureCodesJson = source.EnabledFeatureCodesJson,
            SelectedFoldersJson = source.SelectedFoldersJson,
            FeatureFolderSelectionsJson = source.FeatureFolderSelectionsJson,
            FeatureCatalogSnapshotsJson = source.FeatureCatalogSnapshotsJson
        };
    }

    private void ApplyEditableSettings(AppSettings draft)
    {
        // Bloc "formulaire": toutes les mutations se font ici au clic Enregistrer.
        Settings.ApiBaseUrl = draft.ApiBaseUrl;
        Settings.AccessKey = draft.AccessKey;
        var uiPasswordChanged = !string.Equals(Settings.UiPassword, draft.UiPassword, StringComparison.Ordinal);
        Settings.UiPassword = draft.UiPassword;
        if (uiPasswordChanged)
            Settings.UiPasswordMustChange = false;
        Settings.SqlServerHost = draft.SqlServerHost;
        Settings.SqlAuthenticationMode = draft.SqlAuthenticationMode;
        Settings.SqlUserName = draft.SqlUserName;
        Settings.SqlPassword = draft.SqlPassword;
        Settings.SqlEncryptMode = draft.SqlEncryptMode;
        Settings.SqlTrustServerCertificate = draft.SqlTrustServerCertificate;
        Settings.SqlConnectTimeoutSeconds = draft.SqlConnectTimeoutSeconds;
        Settings.DefaultDatabase = draft.DefaultDatabase;
        Settings.LocalPort = draft.LocalPort;
        Settings.ExposureMode = draft.ExposureMode;
        Settings.ManualTunnelUrl = draft.ManualTunnelUrl;
        Settings.ExposureProvider = draft.ExposureProvider;
        Settings.LaunchAtStartup = draft.LaunchAtStartup;
        Settings.SftpHost = draft.SftpHost;
        Settings.SftpPort = draft.SftpPort;
        Settings.SftpUsername = draft.SftpUsername;
        Settings.SftpPassword = draft.SftpPassword;
        Settings.SftpRemotePath = draft.SftpRemotePath;
        OnPropertyChanged(nameof(Settings));
    }

    private static bool HasChanged(string? before, string? after)
        => !string.Equals((before ?? string.Empty).Trim(), (after ?? string.Empty).Trim(), StringComparison.Ordinal);

    private static bool ShouldRefreshApi(AppSettings before, AppSettings after)
        => HasChanged(before.ApiBaseUrl, after.ApiBaseUrl) || HasChanged(before.AccessKey, after.AccessKey);

    private static bool ShouldRefreshSql(AppSettings before, AppSettings after)
        => HasChanged(before.SqlServerHost, after.SqlServerHost) ||
           HasChanged(before.SqlAuthenticationMode, after.SqlAuthenticationMode) ||
           HasChanged(before.SqlUserName, after.SqlUserName) ||
           HasChanged(before.SqlPassword, after.SqlPassword) ||
           HasChanged(before.SqlEncryptMode, after.SqlEncryptMode) ||
           before.SqlTrustServerCertificate != after.SqlTrustServerCertificate ||
           HasChanged(before.SqlConnectTimeoutSeconds, after.SqlConnectTimeoutSeconds) ||
           HasChanged(before.DefaultDatabase, after.DefaultDatabase);

    private static bool ShouldRefreshTunnel(AppSettings before, AppSettings after)
        => HasChanged(before.LocalPort, after.LocalPort) ||
           HasChanged(before.ExposureMode, after.ExposureMode) ||
           HasChanged(before.ManualTunnelUrl, after.ManualTunnelUrl) ||
           HasChanged(before.ExposureProvider, after.ExposureProvider);

    private Dictionary<string, string> BuildSettingsMap()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["URL API"] = (Settings.ApiBaseUrl ?? string.Empty).Trim(),
            ["Clé d'accès"] = MaskIfSensitive("Clé d'accès", Settings.AccessKey),
            ["Mot de passe interface"] = MaskIfSensitive("Mot de passe interface", Settings.UiPassword),
            ["Serveur SQL"] = (Settings.SqlServerHost ?? string.Empty).Trim(),
            ["Auth SQL"] = (Settings.SqlAuthenticationMode ?? string.Empty).Trim(),
            ["Utilisateur SQL admin"] = (Settings.SqlUserName ?? string.Empty).Trim(),
            ["Mot de passe SQL admin"] = MaskIfSensitive("Mot de passe SQL admin", Settings.SqlPassword),
            ["Utilisateur SQL OXYDRIVER"] = (Settings.SqlRuntimeUserName ?? string.Empty).Trim(),
            ["Chiffrement SQL"] = (Settings.SqlEncryptMode ?? string.Empty).Trim(),
            ["Trust Certificat SQL"] = Settings.SqlTrustServerCertificate ? "Oui" : "Non",
            ["Timeout SQL"] = (Settings.SqlConnectTimeoutSeconds ?? string.Empty).Trim(),
            ["Base SQL par défaut"] = (Settings.DefaultDatabase ?? string.Empty).Trim(),
            ["Port local"] = (Settings.LocalPort ?? string.Empty).Trim(),
            ["Mode exposition"] = (Settings.ExposureMode ?? string.Empty).Trim(),
            ["URL tunnel manuel"] = (Settings.ManualTunnelUrl ?? string.Empty).Trim(),
            ["Provider exposition"] = (Settings.ExposureProvider ?? string.Empty).Trim(),
            ["Démarrage Windows"] = Settings.LaunchAtStartup ? "Oui" : "Non",
            ["Hôte SFTP"] = (Settings.SftpHost ?? string.Empty).Trim(),
            ["Port SFTP"] = (Settings.SftpPort ?? string.Empty).Trim(),
            ["Utilisateur SFTP"] = (Settings.SftpUsername ?? string.Empty).Trim(),
            ["Mot de passe SFTP"] = MaskIfSensitive("Mot de passe SFTP", Settings.SftpPassword),
            ["Chemin SFTP"] = (Settings.SftpRemotePath ?? string.Empty).Trim(),
            ["Dossiers client"] = (Settings.SelectedFoldersJson ?? "[]").Trim(),
            ["Fonctionnalités actives"] = (Settings.EnabledFeatureCodesJson ?? "[]").Trim(),
            ["Config dossiers fonctionnalités"] = (Settings.FeatureFolderSelectionsJson ?? "{}").Trim()
        };
    }

    private static string BuildSettingsSnapshot(Dictionary<string, string> map)
    {
        return JsonSerializer.Serialize(map.OrderBy(k => k.Key).ToDictionary(k => k.Key, v => v.Value));
    }

    private void RefreshSettingsDirtyState(bool force = false)
    {
        var current = BuildSettingsSnapshot(BuildSettingsMap());
        var dirty = !string.Equals(current, _savedSettingsSnapshot, StringComparison.Ordinal);
        if (!force && dirty == _isSettingsDirty) return;
        _isSettingsDirty = dirty;
    }

    private void LogSettingsChanges(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var keys = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        foreach (var key in keys)
        {
            var oldVal = before.TryGetValue(key, out var o) ? o : string.Empty;
            var newVal = after.TryGetValue(key, out var n) ? n : string.Empty;
            if (string.Equals(oldVal, newVal, StringComparison.Ordinal))
                continue;
            changed++;
            LogUtility($"Paramètre modifié: {key}: '{oldVal}' -> '{newVal}'");
        }
        if (changed == 0)
            LogUtility("Paramétrage enregistré: aucune modification détectée.");
    }

    private static string MaskIfSensitive(string key, string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (!key.Contains("passe", StringComparison.OrdinalIgnoreCase) &&
            !key.Contains("clé", StringComparison.OrdinalIgnoreCase))
            return v;
        return string.IsNullOrEmpty(v) ? "<vide>" : "******";
    }

    private bool RequireSavedSettings(string actionLabel)
    {
        if (!_isSettingsDirty) return false;
        var msg = "Paramétrage modifié non enregistré. Clique d'abord sur 'Enregistrer'.";
        switch (actionLabel)
        {
            case "test SQL":
                SqlTestStatus = msg;
                break;
            case "démarrage tunnel":
                TunnelStatus = msg;
                break;
            case "synchronisation API":
                SyncStatus = msg;
                break;
            case "mise à jour utilitaire":
                UpdateStatus = msg;
                break;
            case "test SFTP":
                SftpStatus = msg;
                break;
            case "démarrage auto":
                UpdateStatus = msg;
                break;
            case "token client":
                ClientTokenStatus = msg;
                break;
        }
        LogUtility($"{actionLabel}: {msg}");
        return true;
    }

    private Task CheckUpdatesAsync()
    {
        if (RequireSavedSettings("mise à jour utilitaire"))
            return Task.CompletedTask;
        if (!string.IsNullOrWhiteSpace(_pendingUpdateDownloadUrl))
            return DownloadPendingUpdateAsync();
        return CheckUpdatesWithLoaderAsync();
    }

    private async Task CheckUpdatesWithLoaderAsync()
    {
        IsCheckingUpdates = true;
        UpdateStatus = "Recherche de mise à jour...";
        try
        {
            await CheckUpdatesCoreAsync(interactivePrompt: false);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private async Task CheckUpdatesCoreAsync(bool interactivePrompt)
    {
        try
        {
            var result = await _api.CheckUpdateAsync(Settings.ApiBaseUrl, Settings.AppVersion, Settings.AccessKey);
            if (!result.IsSuccess)
            {
                ResetPendingUpdate();
                UpdateStatus = $"Erreur MAJ: {result.Message}";
                LogUtility(UpdateStatus);
                return;
            }

            if (!result.HasUpdate)
            {
                ResetPendingUpdate();
                UpdateStatus = $"Aucune mise à jour (actuel: {Settings.AppVersion}).";
                LogUtility(UpdateStatus);
                return;
            }

            UpdateStatus = $"Mise à jour disponible: {result.LatestVersion}";
            LogUtility(UpdateStatus);
            _pendingUpdateVersion = result.LatestVersion;
            _pendingUpdateDownloadUrl = string.IsNullOrWhiteSpace(result.DownloadUrl) ? null : result.DownloadUrl;
            _pendingUpdateSftp = result.Sftp;
            _pendingUpdateChangeLines = await BuildPendingUpdateChangeLinesAsync(Settings.AppVersion, _pendingUpdateVersion ?? string.Empty);
            if (string.IsNullOrWhiteSpace(_pendingUpdateDownloadUrl) && _pendingUpdateSftp is null)
            {
                UpdateStatus = "Mise à jour trouvée mais lien de téléchargement indisponible (configuration API).";
                LogUtility(UpdateStatus);
            }
            OnPropertyChanged(nameof(UpdateActionButtonText));

            if (interactivePrompt && !string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                var confirm = System.Windows.MessageBox.Show(
                    $"Version {result.LatestVersion} disponible. Télécharger maintenant ?",
                    "Mise à jour OXYDRIVER",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information
                );
                if (confirm == System.Windows.MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = result.DownloadUrl,
                        UseShellExecute = true
                    });
                    LogUtility($"Téléchargement lancé: {result.DownloadUrl}");
                }
            }
        }
        catch (Exception ex)
        {
            ResetPendingUpdate();
            UpdateStatus = $"Erreur MAJ: {ex.Message}";
            LogUtility(UpdateStatus);
        }
    }

    private async Task DownloadPendingUpdateAsync()
    {
        if (!ConfirmUpdateWithFeatureChanges())
        {
            UpdateStatus = "Téléchargement annulé par l'utilisateur.";
            LogUtility(UpdateStatus);
            return;
        }

        UpdateProgressWindow? progressWindow = null;
        RunOnUi(() =>
        {
            progressWindow = new UpdateProgressWindow();
            progressWindow.Show();
            progressWindow.UpdateProgress(-1, "Initialisation...");
        });

        if (_pendingUpdateSftp is not null)
        {
            var ok = await TryDownloadViaSftpAsync(
                _pendingUpdateSftp,
                _pendingUpdateVersion ?? "unknown",
                (percent, status) => RunOnUi(() => progressWindow?.UpdateProgress(percent, status)));
            if (ok)
            {
                RunOnUi(() => progressWindow?.Close());
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(_pendingUpdateDownloadUrl))
        {
            UpdateStatus = "URL de téléchargement indisponible.";
            LogUtility(UpdateStatus);
            RunOnUi(() => progressWindow?.Close());
            return;
        }

        var downloaded = await DownloadUpdatePackageAsync(
            _pendingUpdateDownloadUrl,
            _pendingUpdateVersion ?? "unknown",
            (percent, status) => RunOnUi(() => progressWindow?.UpdateProgress(percent, status))
        );
        if (string.IsNullOrWhiteSpace(downloaded))
        {
            RunOnUi(() => progressWindow?.Close());
            return;
        }

        if (TryStartInPlaceUpdate(downloaded))
        {
            RunOnUi(() => progressWindow?.Close());
            return;
        }
        if (TryStartInstallerUpdate(downloaded))
        {
            RunOnUi(() => progressWindow?.Close());
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = downloaded, UseShellExecute = true });
        UpdateStatus = $"Téléchargement terminé: {downloaded}";
        LogUtility(UpdateStatus);
        RunOnUi(() => progressWindow?.Close());
    }

    private async Task<bool> TryDownloadViaSftpAsync(SftpConnectionInfo info, string version, Action<double, string>? onProgress = null)
    {
        try
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OXYDRIVER", "updates");
            System.IO.Directory.CreateDirectory(tempDir);
            var ext = System.IO.Path.GetExtension(info.RemotePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
            var localFile = System.IO.Path.Combine(tempDir, $"OXYDRIVER-{version}{ext}");
            await Task.Run(() =>
            {
                using var client = new SftpClient(info.Host, info.Port, info.Username, info.Password);
                client.Connect();
                var requested = (info.RemotePath ?? string.Empty).Trim();
                var fileNameOnly = System.IO.Path.GetFileName(requested.Replace('\\', '/'));
                var candidates = new[]
                {
                    requested,
                    requested.TrimStart('/'),
                    fileNameOnly
                }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

                string? found = null;
                foreach (var candidate in candidates)
                {
                    if (client.Exists(candidate))
                    {
                        found = candidate;
                        break;
                    }
                }
                if (found is null)
                    throw new InvalidOperationException($"Fichier distant introuvable. Essais: {string.Join(", ", candidates)}");

                using var fs = System.IO.File.Create(localFile);
                var total = 0UL;
                try
                {
                    var remoteSize = client.GetAttributes(found).Size;
                    if (remoteSize > 0)
                        total = (ulong)remoteSize;
                }
                catch { /* ignore */ }
                onProgress?.Invoke(total > 0 ? 0 : -1, "Téléchargement SFTP démarré...");
                client.DownloadFile(found, fs, downloadedBytes =>
                {
                    if (total > 0)
                    {
                        var p = (double)downloadedBytes / total * 100.0;
                        var status = $"{downloadedBytes / 1024 / 1024} / {total / 1024 / 1024} MB";
                        onProgress?.Invoke(p, status);
                    }
                    else
                    {
                        var status = $"{downloadedBytes / 1024 / 1024} MB téléchargés";
                        onProgress?.Invoke(-1, status);
                    }
                });
                client.Disconnect();
            });
            onProgress?.Invoke(100, "Téléchargement SFTP terminé.");
            UpdateStatus = $"Téléchargement SFTP terminé: {localFile}";
            LogUtility(UpdateStatus);
            if (TryStartInPlaceUpdate(localFile))
                return true;
            if (TryStartInstallerUpdate(localFile))
                return true;
            Process.Start(new ProcessStartInfo { FileName = localFile, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Erreur téléchargement SFTP: {ex.Message}";
            LogUtility(UpdateStatus);
            return false;
        }
    }

    private async Task TestSftpAsync()
    {
        if (RequireSavedSettings("test SFTP"))
            return;
        var host = (Settings.SftpHost ?? string.Empty).Trim();
        var user = (Settings.SftpUsername ?? string.Empty).Trim();
        var pwd = Settings.SftpPassword ?? string.Empty;
        var path = (Settings.SftpRemotePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pwd))
        {
            SftpStatus = "Paramètres SFTP incomplets.";
            LogUtility(SftpStatus);
            return;
        }
        var port = int.TryParse(Settings.SftpPort, out var p) ? p : 22;
        SftpStatus = "Test SFTP en cours...";
        try
        {
            await Task.Run(() =>
            {
                using var client = new SftpClient(host, port, user, pwd);
                client.Connect();
                if (!string.IsNullOrWhiteSpace(path))
                    _ = client.Exists(path);
                client.Disconnect();
            });
            SftpStatus = "Connexion SFTP OK.";
            LogUtility(SftpStatus);
        }
        catch (Exception ex)
        {
            SftpStatus = $"Erreur SFTP: {ex.Message}";
            LogUtility(SftpStatus);
        }
    }

    private bool TryStartInPlaceUpdate(string packagePath)
    {
        try
        {
            var ext = Path.GetExtension(packagePath).ToLowerInvariant();
            if (ext != ".zip") return false;

            var extractDir = Path.Combine(Path.GetTempPath(), "OXYDRIVER", "update-extract", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractDir, true);

            var targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var exePath = Path.Combine(targetDir, "OXYDRIVER.exe");
            var cmdPath = Path.Combine(Path.GetTempPath(), "OXYDRIVER", "apply-update.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(cmdPath)!);

            var script = string.Join(Environment.NewLine, new[]
            {
                "@echo off",
                "setlocal",
                "timeout /t 2 /nobreak >nul",
                $"xcopy /E /I /Y \"{extractDir}\\*\" \"{targetDir}\\\" >nul",
                $"start \"\" \"{exePath}\"",
                "endlocal"
            });
            File.WriteAllText(cmdPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{cmdPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            UpdateStatus = "Mise à jour appliquée, redémarrage en cours...";
            LogUtility(UpdateStatus);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
            return true;
        }
        catch (Exception ex)
        {
            LogUtility($"Erreur application MAJ zip: {ex.Message}");
            return false;
        }
    }

    private bool TryStartInstallerUpdate(string packagePath)
    {
        try
        {
            var ext = Path.GetExtension(packagePath).ToLowerInvariant();
            if (ext != ".exe" && ext != ".msi")
                return false;

            ProcessStartInfo psi;
            if (ext == ".msi")
            {
                psi = new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{packagePath}\" /qn /norestart",
                    UseShellExecute = true
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = packagePath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = true
                };
            }

            Process.Start(psi);
            UpdateStatus = "Installateur lancé, fermeture de l'utilitaire...";
            LogUtility(UpdateStatus);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
            return true;
        }
        catch (Exception ex)
        {
            LogUtility($"Erreur lancement installateur: {ex.Message}");
            return false;
        }
    }

    private async Task<string?> DownloadUpdatePackageAsync(string url, string version, Action<double, string>? onProgress)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                UpdateStatus = "URL de mise à jour invalide.";
                LogUtility(UpdateStatus);
                return null;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "OXYDRIVER", "updates");
            Directory.CreateDirectory(tempDir);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = $"OXYDRIVER-{version}.bin";
            var localPath = Path.Combine(tempDir, fileName);

            using var http = new HttpClient();
            onProgress?.Invoke(0, "Démarrage du téléchargement...");
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            if (!total.HasValue || total.Value <= 0)
                onProgress?.Invoke(-1, "Téléchargement en cours (taille inconnue)...");
            await using var input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var output = File.Create(localPath);
            var buffer = new byte[81920];
            long read = 0;
            int chunk;
            while ((chunk = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, chunk)).ConfigureAwait(false);
                read += chunk;
                if (total.HasValue && total.Value > 0)
                {
                    var p = (double)read / total.Value * 100.0;
                    onProgress?.Invoke(p, $"{read / 1024 / 1024} / {total.Value / 1024 / 1024} MB");
                }
                else
                {
                    onProgress?.Invoke(-1, $"{read / 1024 / 1024} MB téléchargés");
                }
            }
            onProgress?.Invoke(100, "Téléchargement terminé.");
            UpdateStatus = $"Package téléchargé: {localPath}";
            LogUtility(UpdateStatus);
            return localPath;
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Erreur téléchargement HTTP: {ex.Message}";
            LogUtility(UpdateStatus);
            return null;
        }
    }

    private void ResetPendingUpdate()
    {
        _pendingUpdateDownloadUrl = null;
        _pendingUpdateVersion = null;
        _pendingUpdateSftp = null;
        _pendingUpdateChangeLines = [];
        OnPropertyChanged(nameof(UpdateActionButtonText));
    }

    private Task ClearLogsAsync()
    {
        Logs.Clear();
        UtilityLogs.Clear();
        ClientRequestLogs.Clear();
        LogUtility("Logs nettoyés.");
        return Task.CompletedTask;
    }

    private async Task ToggleFeatureAsync()
    {
        var enabled = FeatureToggles.Where(f => f.IsEnabled).Select(f => f.Code).ToArray();
        Settings.EnabledFeatureCodesJson = JsonSerializer.Serialize(enabled);
        _settingsStore.Save(Settings);

        // Apply SQL role memberships immediately when a feature is toggled.
        // If a feature is disabled, the runtime user is removed from its role.
        if (!string.IsNullOrWhiteSpace(Settings.ApiFeatureCatalogJson))
        {
            var rightsOk = await ApplySqlPermissionsForRuntimeUserAsync(
                Settings.ApiFeatureCatalogJson ?? "[]",
                Settings.ApiFeatureCatalogJson ?? "[]");
            if (!rightsOk)
                LogUtility("Attention: rôles SQL non mis à jour après changement de fonctionnalité.");
        }

        _server.UpdateAuthorizationPolicy(Settings);
        RefreshFeaturesFromSettings();
        LogUtility($"Fonctionnalités actives: {string.Join(", ", enabled)}");
        OnPropertyChanged(nameof(ActiveFeatureCount));
    }

    private async Task RefreshSqlDebugAsync()
    {
        SqlDebugStatus = "Audit SQL en cours…";
        SqlDebugRoles.Clear();
        SqlDebugPermissions.Clear();
        try
        {
            var runtimeUser = (Settings.SqlRuntimeUserName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(runtimeUser))
            {
                SqlDebugStatus = "Utilisateur runtime vide.";
                return;
            }

            var adminBuilder = new SqlConnectionStringBuilder
            {
                DataSource = (Settings.SqlServerHost ?? string.Empty).Trim(),
                InitialCatalog = "master",
                Encrypt = ParseEncryptMode(Settings.SqlEncryptMode),
                TrustServerCertificate = Settings.SqlTrustServerCertificate,
                ConnectTimeout = ParseTimeout(Settings.SqlConnectTimeoutSeconds),
                ApplicationName = "OXYDRIVER-Debug-Audit",
                IntegratedSecurity = false,
                UserID = Settings.SqlUserName,
                Password = Settings.SqlPassword
            };

            var targetDbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(Settings.DefaultDatabase))
                targetDbs.Add(Settings.DefaultDatabase.Trim());
            var features = JsonSerializer.Deserialize<FeatureDefinition[]>(
                Settings.ApiFeatureCatalogJson ?? "[]",
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            foreach (var db in features
                .SelectMany(f => f.Resources)
                .Select(r => (r.Database ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                targetDbs.Add(db);
            }

            await using var conn = new SqlConnection(adminBuilder.ConnectionString);
            await conn.OpenAsync();
            var existingOnServer = await SqlServerCatalog.ListDatabaseNamesAsync(conn);

            await using (var loginCmd = conn.CreateCommand())
            {
                loginCmd.CommandText = "SELECT COUNT(1) FROM sys.sql_logins WHERE name = @user";
                loginCmd.Parameters.AddWithValue("@user", runtimeUser);
                var loginExists = Convert.ToInt32(await loginCmd.ExecuteScalarAsync()) > 0;
                SqlDebugStatus = loginExists
                    ? $"Utilisateur SQL '{runtimeUser}' trouvé."
                    : $"Utilisateur SQL '{runtimeUser}' introuvable.";
                if (!loginExists)
                {
                    LogUtility(SqlDebugStatus);
                    return;
                }
            }

            foreach (var db in targetDbs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!existingOnServer.Contains(db))
                {
                    LogUtility($"Audit SQL: base absente, ignorée: {db}");
                    continue;
                }

                conn.ChangeDatabase(db);

                bool userExistsInDb;
                await using (var userCmd = conn.CreateCommand())
                {
                    userCmd.CommandText = "SELECT COUNT(1) FROM sys.database_principals WHERE name = @user";
                    userCmd.Parameters.AddWithValue("@user", runtimeUser);
                    userExistsInDb = Convert.ToInt32(await userCmd.ExecuteScalarAsync()) > 0;
                }
                if (!userExistsInDb)
                {
                    SqlDebugRoles.Add(new SqlDebugRoleLine
                    {
                        Database = db,
                        RoleName = "—",
                        IsMember = false,
                        Note = $"Utilisateur {runtimeUser} absent dans la base"
                    });
                    continue;
                }

                await using (var rolesCmd = conn.CreateCommand())
                {
                    rolesCmd.CommandText = @"
SELECT r.name AS role_name,
       CASE WHEN drm.member_principal_id IS NULL THEN 0 ELSE 1 END AS is_member
FROM sys.database_principals r
LEFT JOIN sys.database_role_members drm ON drm.role_principal_id = r.principal_id
LEFT JOIN sys.database_principals u
       ON drm.member_principal_id = u.principal_id
      AND u.name = @user
WHERE r.type = 'R'
  AND r.name LIKE 'OXYDRIVER_FEAT_%'
ORDER BY r.name;";
                    rolesCmd.Parameters.AddWithValue("@user", runtimeUser);
                    await using var reader = await rolesCmd.ExecuteReaderAsync();
                    var hasRoles = false;
                    while (await reader.ReadAsync())
                    {
                        hasRoles = true;
                        SqlDebugRoles.Add(new SqlDebugRoleLine
                        {
                            Database = db,
                            RoleName = reader.GetString(0),
                            IsMember = reader.GetInt32(1) == 1,
                            Note = reader.GetInt32(1) == 1 ? "Actif pour l'utilisateur" : "Utilisateur non membre"
                        });
                    }
                    if (!hasRoles)
                    {
                        SqlDebugRoles.Add(new SqlDebugRoleLine
                        {
                            Database = db,
                            RoleName = "—",
                            IsMember = false,
                            Note = "Aucun rôle OXYDRIVER_FEAT_%"
                        });
                    }
                }

                await using (var permsCmd = conn.CreateCommand())
                {
                    permsCmd.CommandText = @"
SELECT pr.name AS principal_name,
       pe.permission_name,
       pe.state_desc,
       OBJECT_SCHEMA_NAME(pe.major_id) AS schema_name,
       OBJECT_NAME(pe.major_id) AS object_name,
       cl.name AS column_name
FROM sys.database_permissions pe
JOIN sys.database_principals pr ON pe.grantee_principal_id = pr.principal_id
LEFT JOIN sys.columns cl ON cl.object_id = pe.major_id AND cl.column_id = pe.minor_id
WHERE (pr.name = @user OR pr.name LIKE 'OXYDRIVER_FEAT_%')
  AND pe.class_desc = 'OBJECT_OR_COLUMN'
ORDER BY pr.name, object_name, column_name, pe.permission_name;";
                    permsCmd.Parameters.AddWithValue("@user", runtimeUser);
                    await using var pReader = await permsCmd.ExecuteReaderAsync();
                    while (await pReader.ReadAsync())
                    {
                        SqlDebugPermissions.Add(new SqlDebugPermissionLine
                        {
                            Database = db,
                            Principal = pReader.IsDBNull(0) ? string.Empty : pReader.GetString(0),
                            Permission = pReader.IsDBNull(1) ? string.Empty : pReader.GetString(1),
                            State = pReader.IsDBNull(2) ? string.Empty : pReader.GetString(2),
                            Target = $"{(pReader.IsDBNull(3) ? "dbo" : pReader.GetString(3))}.{(pReader.IsDBNull(4) ? "?" : pReader.GetString(4))}",
                            Column = pReader.IsDBNull(5) ? "*" : pReader.GetString(5)
                        });
                    }
                }
            }

            conn.ChangeDatabase("master");
            RebuildSqlDebugFilterOptions();
            ApplySqlDebugFilters();
            SqlDebugStatus = $"Audit SQL OK ({SqlDebugRoles.Count} rôles, {SqlDebugPermissions.Count} permissions).";
            LogUtility(SqlDebugStatus);
        }
        catch (Exception ex)
        {
            SqlDebugStatus = $"Erreur audit SQL: {ex.Message}";
            LogUtility(SqlDebugStatus);
        }
    }

    private void RebuildSqlDebugFilterOptions()
    {
        var currentDb = SqlDebugSelectedDatabase;
        var currentFeature = SqlDebugSelectedFeature;
        var currentPerm = SqlDebugSelectedPermission;
        var currentState = SqlDebugSelectedState;

        SqlDebugDatabaseOptions.Clear();
        SqlDebugDatabaseOptions.Add("(Toutes)");
        foreach (var db in SqlDebugPermissions.Select(x => x.Database)
            .Concat(SqlDebugRoles.Select(x => x.Database))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            SqlDebugDatabaseOptions.Add(db);
        }

        SqlDebugFeatureOptions.Clear();
        SqlDebugFeatureOptions.Add("(Toutes)");
        foreach (var feature in SqlDebugRoles.Select(x => x.FeatureCode)
            .Concat(SqlDebugPermissions.Select(x => x.FeatureCode))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            SqlDebugFeatureOptions.Add(feature);
        }

        SqlDebugPermissionOptions.Clear();
        SqlDebugPermissionOptions.Add("(Toutes)");
        foreach (var permission in SqlDebugPermissions.Select(x => x.Permission)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            SqlDebugPermissionOptions.Add(permission);
        }

        SqlDebugStateOptions.Clear();
        SqlDebugStateOptions.Add("(Tous)");
        foreach (var state in SqlDebugPermissions.Select(x => x.State)
            .Concat(SqlDebugRoles.Select(x => x.MembershipText))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            SqlDebugStateOptions.Add(state);
        }

        SqlDebugSelectedDatabase = SqlDebugDatabaseOptions.Contains(currentDb) ? currentDb : "(Toutes)";
        SqlDebugSelectedFeature = SqlDebugFeatureOptions.Contains(currentFeature) ? currentFeature : "(Toutes)";
        SqlDebugSelectedPermission = SqlDebugPermissionOptions.Contains(currentPerm) ? currentPerm : "(Toutes)";
        SqlDebugSelectedState = SqlDebugStateOptions.Contains(currentState) ? currentState : "(Tous)";
    }

    private void ApplySqlDebugFilters()
    {
        SqlDebugRolesView.Refresh();
        SqlDebugPermissionsView.Refresh();
    }

    private bool MatchSqlDebugRole(SqlDebugRoleLine? row)
    {
        if (row is null) return false;
        if (!MatchesFilter(SqlDebugSelectedDatabase, row.Database, "(Toutes)")) return false;
        if (!MatchesFilter(SqlDebugSelectedFeature, row.FeatureCode, "(Toutes)")) return false;
        if (!MatchesFilter(SqlDebugSelectedState, row.MembershipText, "(Tous)")) return false;
        return true;
    }

    private bool MatchSqlDebugPermission(SqlDebugPermissionLine? row)
    {
        if (row is null) return false;
        if (!MatchesFilter(SqlDebugSelectedDatabase, row.Database, "(Toutes)")) return false;
        if (!MatchesFilter(SqlDebugSelectedFeature, row.FeatureCode, "(Toutes)")) return false;
        if (!MatchesFilter(SqlDebugSelectedPermission, row.Permission, "(Toutes)")) return false;
        if (!MatchesFilter(SqlDebugSelectedState, row.State, "(Tous)")) return false;
        if (!ContainsIgnoreCase(row.TableName, SqlDebugTableFilter)) return false;
        if (!ContainsIgnoreCase(row.Column, SqlDebugColumnFilter)) return false;
        return true;
    }

    private static bool MatchesFilter(string selected, string actual, string allValue)
    {
        if (string.Equals(selected, allValue, StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(selected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIgnoreCase(string source, string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return true;
        return (source ?? string.Empty).Contains(input.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFeaturesFromSettings()
    {
        BuildFeatureToggles();
        FeatureItems.Clear();
        var raw = Settings.ApiFeatureCatalogJson;
        if (string.IsNullOrWhiteSpace(raw))
        {
            FeatureItems.Add("Aucune fonctionnalité synchronisée.");
            OnPropertyChanged(nameof(ActiveFeatureCount));
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in doc.RootElement.EnumerateArray())
                {
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() : "feature";
                    var code = f.TryGetProperty("code", out var c) ? c.GetString() : "";
                    var resources = f.TryGetProperty("resources", out var r) ? r.GetArrayLength() : 0;
                    FeatureItems.Add($"{name} ({code}) - ressources: {resources}");
                }
            }
            else
            {
                FeatureItems.Add(raw);
            }
        }
        catch
        {
            FeatureItems.Add(raw);
        }
        OnPropertyChanged(nameof(ActiveFeatureCount));
    }

    private void UpdateFeatureCatalogChanges(string oldRaw, string newRaw)
    {
        FeatureCatalogChanges.Clear();

        var oldDefs = ParseCatalog(oldRaw);
        var newDefs = ParseCatalog(newRaw);
        var oldSet = BuildExposureSet(oldDefs, includeDatabase: true);
        var newSet = BuildExposureSet(newDefs, includeDatabase: true);
        var oldSetNoDb = BuildExposureSet(oldDefs, includeDatabase: false);
        var newSetNoDb = BuildExposureSet(newDefs, includeDatabase: false);

        var oldDbSet = oldDefs
            .SelectMany(d => d.Resources)
            .Select(r => (r.Database ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newDbSet = newDefs
            .SelectMany(d => d.Resources)
            .Select(r => (r.Database ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (oldSetNoDb.SetEquals(newSetNoDb) && !oldDbSet.SetEquals(newDbSet))
        {
            foreach (var removedDb in oldDbSet.Except(newDbSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                FeatureCatalogChanges.Add(new FeatureCatalogChangeLine
                {
                    Message = $"BASE RETIREE: {removedDb}",
                    Foreground = System.Windows.Media.Brushes.Red,
                    IsRemoved = true
                });
            }
            foreach (var addedDb in newDbSet.Except(oldDbSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                FeatureCatalogChanges.Add(new FeatureCatalogChangeLine
                {
                    Message = $"BASE AJOUTEE: {addedDb}",
                    Foreground = System.Windows.Media.Brushes.Green,
                    IsRemoved = false
                });
            }
        }
        else
        {
            var removed = oldSet.Except(newSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
            var added = newSet.Except(oldSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

            foreach (var line in removed)
            {
                FeatureCatalogChanges.Add(new FeatureCatalogChangeLine
                {
                    Message = $"RETIRÉ: {line}",
                    Foreground = System.Windows.Media.Brushes.Red,
                    IsRemoved = true
                });
            }
            foreach (var line in added)
            {
                FeatureCatalogChanges.Add(new FeatureCatalogChangeLine
                {
                    Message = $"NOUVEAU: {line}",
                    Foreground = System.Windows.Media.Brushes.Green,
                    IsRemoved = false
                });
            }
        }

        if (FeatureCatalogChanges.Count == 0)
        {
            FeatureCatalogAlertText = "Aucune évolution de fonctionnalités détectée sur la dernière sync.";
            IsFeatureCatalogChangesExpanded = false;
            LogUtility("Aucune évolution de fonctionnalités détectée.");
        }
        else
        {
            FeatureCatalogAlertText = $"{FeatureCatalogChanges.Count} évolution(s) détectée(s) dans le portail client.";
            IsFeatureCatalogChangesExpanded = false;
            LogUtility(FeatureCatalogAlertText);
            foreach (var c in FeatureCatalogChanges.Take(30))
                LogUtility(c.Message);
        }
    }

    private void BuildFeatureToggles()
    {
        FeatureToggles.Clear();
        var enabled = ReadEnabledFeatureCodes();
        var catalogRaw = Settings.ApiFeatureCatalogJson;
        if (string.IsNullOrWhiteSpace(catalogRaw)) return;

        try
        {
            var defs = JsonSerializer.Deserialize<FeatureDefinition[]>(catalogRaw, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                       ?? [];
            foreach (var def in defs)
            {
                var recap = BuildRecap(def);
                var item = new FeatureToggleItem
                {
                    Name = def.Name,
                    Code = def.Code,
                    Description = def.Description,
                    SiteUrl = def.SiteUrl ?? string.Empty,
                    Recap = recap,
                    IsEnabled = enabled.Contains(def.Code)
                };
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FeatureToggleItem.IsEnabled))
                        _ = ToggleFeatureAsync();
                };
                FeatureToggles.Add(item);
            }
            OnPropertyChanged(nameof(ActiveFeatureCount));
        }
        catch
        {
            // Ignore catalog parse errors in UI list
        }
    }

    private string BuildRecap(FeatureDefinition def)
    {
        var grouped = def.Resources
            .GroupBy(r => BuildResourceSignatureWithoutDatabase(r), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var blocks = grouped.Select(g =>
        {
            var first = g.First();
            var dbs = g.Select(x => (x.Database ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var readWrite = first.Columns
                .Where(c => (c.Rights ?? []).Any(x => string.Equals(x, "write", StringComparison.OrdinalIgnoreCase)))
                .Select(c => c.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var readOnly = first.Columns
                .Where(c =>
                {
                    var rights = (c.Rights ?? []).Select(x => x.ToLowerInvariant()).ToArray();
                    return rights.Contains("read") && !rights.Contains("write");
                })
                .Select(c => c.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var lines = new List<string>
            {
                dbs.Length <= 1
                    ? $"[{(dbs.Length == 1 ? dbs[0] : "-")}] {first.Table}"
                    : $"[{dbs.Length} bases: {string.Join(", ", dbs)}] {first.Table}"
            };
            if (readWrite.Length > 0)
                lines.Add($"  RW: {string.Join(", ", readWrite)}");
            if (readOnly.Length > 0)
                lines.Add($"  R : {string.Join(", ", readOnly)}");
            return string.Join(Environment.NewLine, lines);
        });
        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    private HashSet<string> ReadEnabledFeatureCodes()
    {
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(Settings.EnabledFeatureCodesJson ?? "[]") ?? [];
            return arr.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    private static FeatureDefinition[] ParseCatalog(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return JsonSerializer.Deserialize<FeatureDefinition[]>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static HashSet<string> BuildExposureSet(IEnumerable<FeatureDefinition> defs, bool includeDatabase)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in defs)
        {
            foreach (var r in def.Resources)
            {
                foreach (var c in r.Columns)
                {
                    var rights = string.Join("/", (c.Rights ?? []).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                    var dbPrefix = includeDatabase ? $"{r.Database}." : string.Empty;
                    var line = $"{dbPrefix}{r.Table}.{c.Name} [{rights}]";
                    set.Add(line);
                }
            }
        }
        return set;
    }

    private static string BuildResourceSignatureWithoutDatabase(FeatureResource r)
    {
        var cols = (r.Columns ?? [])
            .Select(c =>
            {
                var rights = string.Join("/", (c.Rights ?? []).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                return $"{(c.Name ?? string.Empty).Trim()}[{rights}]";
            })
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return $"{(r.Table ?? string.Empty).Trim()}|{string.Join(";", cols)}";
    }

    private async Task<string[]> BuildPendingUpdateChangeLinesAsync(string currentVersion, string targetVersion)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
            return [];

        var currentCatalogRaw = ResolveCatalogSnapshotForVersion(currentVersion) ?? Settings.ApiFeatureCatalogJson ?? string.Empty;
        var currentDefs = ParseCatalog(currentCatalogRaw);
        var targetDefs = await _api.GetFeatureCatalogForVersionAsync(Settings, targetVersion) ?? [];
        if (targetDefs.Length == 0)
            return ["Catalogue cible indisponible: le téléchargement reste possible sans aperçu des évolutions."];

        SaveFeatureCatalogSnapshot(targetVersion, JsonSerializer.Serialize(targetDefs));
        return BuildUpdateEvolutionLines(currentDefs, targetDefs);
    }

    private bool ConfirmUpdateWithFeatureChanges()
    {
        var lines = _pendingUpdateChangeLines?.Length > 0
            ? _pendingUpdateChangeLines
            : ["Aucune évolution détectée sur les fonctionnalités exposées."];
        var dialog = new UpdateFeatureChangesWindow(_pendingUpdateVersion ?? "disponible", lines)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true;
    }

    private string[] BuildUpdateEvolutionLines(FeatureDefinition[] currentDefs, FeatureDefinition[] targetDefs)
    {
        var lines = new List<string>();
        var oldByCode = currentDefs.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        var newByCode = targetDefs.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        var allCodes = oldByCode.Keys
            .Union(newByCode.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var code in allCodes)
        {
            var hasOld = oldByCode.TryGetValue(code, out var oldDef);
            var hasNew = newByCode.TryGetValue(code, out var newDef);
            var featureName = newDef?.Name ?? oldDef?.Name ?? code;

            lines.Add($"=== {featureName} ({code}) ===");

            if (!hasOld && hasNew)
            {
                lines.Add("[AJOUT] Fonctionnalité ajoutée.");
                foreach (var resource in newDef!.Resources.OrderBy(r => r.Database).ThenBy(r => r.Table))
                {
                    var tableKey = $"{resource.Database}.{resource.Table}";
                    lines.Add($"[AJOUT] Table: {tableKey}");
                    AppendResourceColumns(lines, resource, tableKey, "[AJOUT]");
                }
                lines.Add(string.Empty);
                continue;
            }
            if (hasOld && !hasNew)
            {
                lines.Add("[RETRAIT] Fonctionnalité retirée.");
                foreach (var resource in oldDef!.Resources.OrderBy(r => r.Database).ThenBy(r => r.Table))
                {
                    var tableKey = $"{resource.Database}.{resource.Table}";
                    lines.Add($"[RETRAIT] Table: {tableKey}");
                    AppendResourceColumns(lines, resource, tableKey, "[RETRAIT]");
                }
                lines.Add(string.Empty);
                continue;
            }

            var oldResources = oldDef!.Resources.ToDictionary(
                r => $"{r.Database}.{r.Table}",
                r => r,
                StringComparer.OrdinalIgnoreCase
            );
            var newResources = newDef!.Resources.ToDictionary(
                r => $"{r.Database}.{r.Table}",
                r => r,
                StringComparer.OrdinalIgnoreCase
            );

            var oldDbSet = oldDef.Resources.Select(r => r.Database).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newDbSet = newDef.Resources.Select(r => r.Database).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var addedDb in newDbSet.Except(oldDbSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                lines.Add($"[AJOUT] Base: {addedDb}");
            foreach (var removedDb in oldDbSet.Except(newDbSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                lines.Add($"[RETRAIT] Base: {removedDb}");

            foreach (var key in newResources.Keys.Except(oldResources.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                lines.Add($"[AJOUT] Table: {key}");
                AppendResourceColumns(lines, newResources[key], key, "[AJOUT]");
            }
            foreach (var key in oldResources.Keys.Except(newResources.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                lines.Add($"[RETRAIT] Table: {key}");
                AppendResourceColumns(lines, oldResources[key], key, "[RETRAIT]");
            }

            foreach (var key in oldResources.Keys.Intersect(newResources.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                var oldCols = oldResources[key].Columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
                var newCols = newResources[key].Columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

                foreach (var col in newCols.Keys.Except(oldCols.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                    lines.Add($"[AJOUT] Colonne: {key}.{col} ({FormatRights(newCols[col].Rights)})");
                foreach (var col in oldCols.Keys.Except(newCols.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                    lines.Add($"[RETRAIT] Colonne: {key}.{col} ({FormatRights(oldCols[col].Rights)})");

                foreach (var col in oldCols.Keys.Intersect(newCols.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                {
                    var oldRights = NormalizeRights(oldCols[col].Rights);
                    var newRights = NormalizeRights(newCols[col].Rights);
                    if (!oldRights.SetEquals(newRights))
                    {
                        lines.Add($"[DROITS MODIFIES] {key}.{col}: {FormatRights(oldCols[col].Rights)} -> {FormatRights(newCols[col].Rights)}");
                    }
                }
            }

            lines.Add(string.Empty);
        }

        if (lines.Count == 0)
            lines.Add("Aucune évolution détectée sur les fonctionnalités exposées.");
        return lines.ToArray();
    }

    private static void AppendResourceColumns(List<string> lines, FeatureResource resource, string tableKey, string prefix)
    {
        foreach (var col in resource.Columns
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"{prefix} Colonne: {tableKey}.{col.Name} ({FormatRights(col.Rights)})");
        }
    }

    private static HashSet<string> NormalizeRights(IEnumerable<string>? rights)
    {
        return (rights ?? [])
            .Select(x => (x ?? string.Empty).Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatRights(IEnumerable<string>? rights)
    {
        var set = NormalizeRights(rights);
        if (set.Contains("read") && set.Contains("write")) return "lecture/ecriture";
        if (set.Contains("write")) return "ecriture";
        if (set.Contains("read")) return "lecture";
        return "aucun";
    }

    private string? ResolveCatalogSnapshotForVersion(string version)
    {
        var map = ReadCatalogSnapshots();
        return map.TryGetValue(version.Trim(), out var raw) ? raw : null;
    }

    private void SaveFeatureCatalogSnapshot(string version, string? catalogRaw)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(catalogRaw))
            return;
        var map = ReadCatalogSnapshots();
        map[version.Trim()] = catalogRaw;
        Settings.FeatureCatalogSnapshotsJson = JsonSerializer.Serialize(map);
        _settingsStore.Save(Settings);
    }

    private Dictionary<string, string> ReadCatalogSnapshots()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Settings.FeatureCatalogSnapshotsJson ?? "{}")
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void OpenFeatureSite(FeatureToggleItem? item)
    {
        var url = item?.SiteUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            LogUtility("Aucune URL de site configurée pour cette fonctionnalité.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogUtility($"Ouverture site impossible: {ex.Message}");
        }
    }

    private void ConfigureFeatureFolders(FeatureToggleItem? item)
    {
        if (item is null) return;
        var allFolders = SelectedFolders
            .Select(f => (f.Name ?? string.Empty).Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allFolders.Length == 0)
        {
            LogUtility("Configure d'abord au moins un dossier client dans Paramétrage.");
            return;
        }

        var current = ReadFeatureFolderSelections();
        var selected = current.TryGetValue(item.Code, out var saved) && saved.Length > 0
            ? saved
            : allFolders;
        var availableFolders = ResolveExistingFoldersForFeature(item.Code, allFolders);

        var dlg = new FeatureFoldersConfigWindow(item.Name, allFolders, selected, availableFolders)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true)
            return;

        current[item.Code] = dlg.SelectedFolders;
        Settings.FeatureFolderSelectionsJson = JsonSerializer.Serialize(current);
        _server.UpdateAuthorizationPolicy(Settings);
        LogUtility($"Configuration dossiers mise à jour pour '{item.Name}': {string.Join(", ", dlg.SelectedFolders)}");
    }

    private string[] ResolveExistingFoldersForFeature(string featureCode, string[] candidateFolders)
    {
        try
        {
            var defs = ParseCatalog(Settings.ApiFeatureCatalogJson ?? string.Empty);
            var feature = defs.FirstOrDefault(x => string.Equals((x.Code ?? string.Empty).Trim(), featureCode, StringComparison.OrdinalIgnoreCase));
            if (feature is null || feature.Resources.Count == 0)
                return candidateFolders;

            var existingDatabases = TryListSqlDatabases();
            if (existingDatabases.Count == 0)
                return candidateFolders;

            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in candidateFolders)
            {
                var matchingDbs = feature.Resources
                    .Select(r => (r.Database ?? string.Empty).Trim())
                    .Where(db => !string.IsNullOrWhiteSpace(db) &&
                                 string.Equals(ExtractFolderFromDatabaseName(db), folder, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (matchingDbs.Any(db => existingDatabases.Contains(db)))
                    available.Add(folder);
            }
            return available.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return candidateFolders;
        }
    }

    private HashSet<string> TryListSqlDatabases()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var adminBuilder = new SqlConnectionStringBuilder
            {
                DataSource = (Settings.SqlServerHost ?? string.Empty).Trim(),
                InitialCatalog = "master",
                Encrypt = ParseEncryptMode(Settings.SqlEncryptMode),
                TrustServerCertificate = Settings.SqlTrustServerCertificate,
                ConnectTimeout = ParseTimeout(Settings.SqlConnectTimeoutSeconds),
                ApplicationName = "OXYDRIVER-FeatureFolders",
                IntegratedSecurity = false,
                UserID = Settings.SqlUserName,
                Password = Settings.SqlPassword
            };
            using var conn = new SqlConnection(adminBuilder.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM master.sys.databases";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                set.Add(reader.GetString(0));
        }
        catch (Exception ex)
        {
            LogUtility($"Impossible de lister les bases SQL pour config dossiers: {ex.Message}");
        }
        return set;
    }

    private static string ExtractFolderFromDatabaseName(string dbName)
    {
        var raw = (dbName ?? string.Empty).Trim();
        var idx = raw.IndexOf('_');
        if (idx < 0 || idx == raw.Length - 1) return string.Empty;
        return raw[(idx + 1)..].Trim().ToUpperInvariant();
    }

    private Dictionary<string, string[]> ReadFeatureFolderSelections()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string[]>>(Settings.FeatureFolderSelectionsJson ?? "{}")
                ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void LogUtility(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        RunOnUi(() =>
        {
            var isError = message.Contains("erreur", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("échec", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("refusé", StringComparison.OrdinalIgnoreCase);
            UtilityLogs.Insert(0, new UtilityLogLine
            {
                Message = line,
                Foreground = isError ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Black
            });
            Logs.Insert(0, $"[UTILITAIRE] {line}");
            TrimLogs();
        });
        _ = PersistSystemLogAsync(message);
    }

    private void LogClient(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        RunOnUi(() => ClientRequestLogs.Insert(0, new ClientLogLine
        {
            Message = line,
            Foreground = System.Windows.Media.Brushes.Black
        }));
        RunOnUi(() =>
        {
            Logs.Insert(0, $"[CLIENT] {line}");
            TrimLogs();
        });
    }

    private void OnClientRequestLogged(ClientRequestLogEntry e)
    {
        var isError = e.StatusCode >= 400;
        var line = $"[{DateTime.Now:HH:mm:ss}] {e.Method} {e.Path}{e.Query} -> {e.StatusCode} ({e.Message})";
        RunOnUi(() => ClientRequestLogs.Insert(0, new ClientLogLine
        {
            Message = line,
            Foreground = isError ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Black
        }));
        RunOnUi(() =>
        {
            Logs.Insert(0, $"[CLIENT] {line}");
            TrimLogs();
        });
        _ = PersistRequestLogAsync(e);
    }

    private void OpenLogHistory()
    {
        var window = new LogHistoryWindow(_appLogStore)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.ShowDialog();
    }

    private async Task PersistSystemLogAsync(string message)
    {
        try
        {
            await _appLogStore.AddSystemLogAsync("action_utilitaire", message);
        }
        catch
        {
            // L'échec d'écriture du log persistent ne doit pas casser le flux UI.
        }
    }

    private async Task PersistRequestLogAsync(ClientRequestLogEntry e)
    {
        try
        {
            var details = $"{e.Method} {e.Path}{e.Query} -> {e.StatusCode} ({e.Message})";
            await _appLogStore.AddRequestLogAsync(
                "requete_client",
                details,
                e.RequestContent,
                e.ResponseContent,
                e.ErrorContent
            );
        }
        catch
        {
            // L'échec d'écriture du log persistent ne doit pas casser le flux UI.
        }
    }

    private void TrimLogs()
    {
        while (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1);
        while (UtilityLogs.Count > 300) UtilityLogs.RemoveAt(UtilityLogs.Count - 1);
        while (ClientRequestLogs.Count > 300) ClientRequestLogs.RemoveAt(ClientRequestLogs.Count - 1);
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.Invoke(action);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async Task ExportBackupAsync(string path)
    {
        try
        {
            _settingsStore.Save(Settings);
            await _backupService.ExportAsync(path, BackupPassword, Settings);
            BackupStatus = $"Backup exporté: {path}";
            LogUtility(BackupStatus);
        }
        catch (Exception ex)
        {
            BackupStatus = $"Erreur backup export: {ex.Message}";
            LogUtility(BackupStatus);
        }
    }

    public async Task ImportBackupAsync(string path)
    {
        try
        {
            var imported = await _backupService.ImportAsync(path, BackupPassword);
            _settingsStore.Save(imported);
            BackupStatus = "Backup importé. Redémarre l'application pour recharger complètement les champs.";
            LogUtility(BackupStatus);
        }
        catch (Exception ex)
        {
            BackupStatus = $"Erreur backup import: {ex.Message}";
            LogUtility(BackupStatus);
        }
    }

    private async Task AutoStartupSequenceAsync()
    {
        var missing = GetMissingSettings();
        if (missing.Count > 0)
        {
            SetStartupStateWarning($"Paramétrage manquant: {string.Join(", ", missing)}");
            LogUtility(HeaderIndicatorText);
            return;
        }

        SetStartupStateStarting();
        LogUtility("Séquence de démarrage automatique en cours.");

        var tunnelOk = await StartTunnelCoreAsync();
        var syncOk = await SyncCoreAsync();
        var sqlOk = await TestSqlCoreAsync();

        if (syncOk && tunnelOk && sqlOk)
        {
            SetStartupStateStarted();
            LogUtility("OXYDRIVER démarré: tous les contrôles sont OK.");
        }
        else
        {
            SetStartupStateError("Erreur de démarrage (voir logs / statuts).");
            LogUtility(HeaderIndicatorText);
        }
    }

    private void RefreshHeaderStateFromCurrentStatuses()
    {
        var missing = GetMissingSettings();
        if (missing.Count > 0)
        {
            SetStartupStateWarning($"Paramétrage manquant: {string.Join(", ", missing)}");
            return;
        }

        var syncOk = SyncStatus.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
        var tunnelOk = TunnelStatus.StartsWith("Tunnel:", StringComparison.OrdinalIgnoreCase);
        var sqlOk = SqlTestStatus.StartsWith("OK", StringComparison.OrdinalIgnoreCase);

        if (syncOk && tunnelOk && sqlOk)
        {
            SetStartupStateStarted();
            return;
        }

        var hasExplicitError =
            SyncStatus.StartsWith("Erreur", StringComparison.OrdinalIgnoreCase) ||
            SyncStatus.StartsWith("Refusé", StringComparison.OrdinalIgnoreCase) ||
            SyncStatus.StartsWith("API injoignable", StringComparison.OrdinalIgnoreCase) ||
            TunnelStatus.StartsWith("Erreur", StringComparison.OrdinalIgnoreCase) ||
            SqlTestStatus.StartsWith("Erreur", StringComparison.OrdinalIgnoreCase);

        if (hasExplicitError)
        {
            SetStartupStateError("Erreur de démarrage (voir logs / statuts).");
            return;
        }

        SetStartupStateWarning("Vérifications incomplètes.");
    }

    private List<string> GetMissingSettings()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Settings.ApiBaseUrl)) missing.Add("URL API");
        if (string.IsNullOrWhiteSpace(Settings.AccessKey)) missing.Add("Clé d'accès");
        if (string.IsNullOrWhiteSpace(Settings.SqlServerHost)) missing.Add("Serveur SQL");
        if (string.IsNullOrWhiteSpace(Settings.DefaultDatabase)) missing.Add("Base SQL");
        if (string.Equals(Settings.ExposureMode, "ManualUrl", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(Settings.ManualTunnelUrl)) missing.Add("URL tunnel manuel");
        var sqlAuth = string.Equals(Settings.SqlAuthenticationMode, "SqlServer", StringComparison.OrdinalIgnoreCase);
        if (sqlAuth && string.IsNullOrWhiteSpace(Settings.SqlUserName)) missing.Add("Utilisateur SQL admin");
        if (sqlAuth && string.IsNullOrWhiteSpace(Settings.SqlPassword)) missing.Add("Mot de passe SQL admin");
        if (!SelectedFolders.Any(f => !string.IsNullOrWhiteSpace(f.Name))) missing.Add("Dossier client");
        return missing;
    }

    private Task AddFolderAsync()
    {
        AddFolder();
        return Task.CompletedTask;
    }

    private Task RemoveFolderAsync()
    {
        RemoveLastFolder();
        return Task.CompletedTask;
    }

    private void AddFolder()
    {
        if (!CanAddFolder) return;
        var folder = new FolderEntry();
        folder.PropertyChanged += OnFolderEntryChanged;
        SelectedFolders.Add(folder);
        PersistFoldersToSettings();
        OnPropertyChanged(nameof(CanAddFolder));
        OnPropertyChanged(nameof(CanRemoveFolder));
    }

    private void RemoveLastFolder()
    {
        if (SelectedFolders.Count <= 1) return;
        var folder = SelectedFolders[SelectedFolders.Count - 1];
        folder.PropertyChanged -= OnFolderEntryChanged;
        SelectedFolders.RemoveAt(SelectedFolders.Count - 1);
        PersistFoldersToSettings();
        OnPropertyChanged(nameof(CanAddFolder));
        OnPropertyChanged(nameof(CanRemoveFolder));
    }

    private void RemoveFolderItem(FolderEntry? folder)
    {
        if (folder is null) return;
        if (SelectedFolders.Count <= 1) return;
        folder.PropertyChanged -= OnFolderEntryChanged;
        _ = SelectedFolders.Remove(folder);
        if (SelectedFolders.Count == 0)
        {
            var fallback = new FolderEntry();
            fallback.PropertyChanged += OnFolderEntryChanged;
            SelectedFolders.Add(fallback);
        }
        PersistFoldersToSettings();
        OnPropertyChanged(nameof(CanAddFolder));
        OnPropertyChanged(nameof(CanRemoveFolder));
    }

    private void SyncFoldersFromSettings()
    {
        SelectedFolders.Clear();
        string[] folders;
        try
        {
            folders = JsonSerializer.Deserialize<string[]>(Settings.SelectedFoldersJson ?? "[]") ?? [];
        }
        catch
        {
            folders = [];
        }

        foreach (var raw in folders.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var folder = new FolderEntry { Name = raw.Trim() };
            folder.PropertyChanged += OnFolderEntryChanged;
            SelectedFolders.Add(folder);
        }

        if (SelectedFolders.Count == 0)
        {
            var folder = new FolderEntry();
            folder.PropertyChanged += OnFolderEntryChanged;
            SelectedFolders.Add(folder);
        }
        PersistFoldersToSettings();
        OnPropertyChanged(nameof(CanAddFolder));
        OnPropertyChanged(nameof(CanRemoveFolder));
    }

    private void OnFolderEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderEntry.Name))
            PersistFoldersToSettings();
        OnPropertyChanged(nameof(CanAddFolder));
        OnPropertyChanged(nameof(CanRemoveFolder));
    }

    private void PersistFoldersToSettings()
    {
        var values = SelectedFolders
            .Select(x => (x.Name ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Settings.SelectedFoldersJson = JsonSerializer.Serialize(values);
    }

    private void OnFoldersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanAddFolder));
        OnPropertyChanged(nameof(CanRemoveFolder));
    }

    private void SetStartupStateStarting()
    {
        HeaderIndicatorBrush = System.Windows.Media.Brushes.LimeGreen;
        HeaderIndicatorText = "Démarrage...";
        HeaderIndicatorOpacity = 1.0;
        if (!_startupBlinkTimer.IsEnabled) _startupBlinkTimer.Start();
    }

    private void SetStartupStateStarted()
    {
        if (_startupBlinkTimer.IsEnabled) _startupBlinkTimer.Stop();
        HeaderIndicatorBrush = System.Windows.Media.Brushes.LimeGreen;
        HeaderIndicatorText = "Démarré";
        HeaderIndicatorOpacity = 1.0;
    }

    private void SetStartupStateWarning(string message)
    {
        if (_startupBlinkTimer.IsEnabled) _startupBlinkTimer.Stop();
        HeaderIndicatorBrush = System.Windows.Media.Brushes.Orange;
        HeaderIndicatorText = message;
        HeaderIndicatorOpacity = 1.0;
    }

    private void SetStartupStateError(string message)
    {
        if (_startupBlinkTimer.IsEnabled) _startupBlinkTimer.Stop();
        HeaderIndicatorBrush = System.Windows.Media.Brushes.Red;
        HeaderIndicatorText = message;
        HeaderIndicatorOpacity = 1.0;
    }

    private async Task TryAutoSyncTunnelMappingAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.ApiBaseUrl) || string.IsNullOrWhiteSpace(Settings.AccessKey))
            return;
        if (string.IsNullOrWhiteSpace(Settings.TunnelPublicUrl))
            return;

        var syncOk = await SyncCoreAsync();
        if (syncOk)
            LogUtility("Mapping token -> URL tunnel synchronisé automatiquement.");
    }
}

public sealed class ClientLogLine
{
    public string Message { get; set; } = string.Empty;
    public System.Windows.Media.Brush Foreground { get; set; } = System.Windows.Media.Brushes.Black;
}

public sealed class UtilityLogLine
{
    public string Message { get; set; } = string.Empty;
    public System.Windows.Media.Brush Foreground { get; set; } = System.Windows.Media.Brushes.Black;
}

public sealed class FeatureCatalogChangeLine
{
    public string Message { get; set; } = string.Empty;
    public System.Windows.Media.Brush Foreground { get; set; } = System.Windows.Media.Brushes.Black;
    public bool IsRemoved { get; set; }
}

public sealed class FeatureToggleItem : INotifyPropertyChanged
{
    private bool _isEnabled;
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SiteUrl { get; set; } = string.Empty;
    public string Recap { get; set; } = string.Empty;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SqlDebugRoleLine
{
    public string Database { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public bool IsMember { get; set; }
    public string MembershipText => IsMember ? "Oui" : "Non";
    public string FeatureCode => RoleName.StartsWith("OXYDRIVER_FEAT_", StringComparison.OrdinalIgnoreCase)
        ? RoleName["OXYDRIVER_FEAT_".Length..]
        : RoleName;
    public string Note { get; set; } = string.Empty;
}

public sealed class SqlDebugPermissionLine
{
    public string Database { get; set; } = string.Empty;
    public string Principal { get; set; } = string.Empty;
    public string Permission { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Column { get; set; } = string.Empty;
    public string FeatureCode => Principal.StartsWith("OXYDRIVER_FEAT_", StringComparison.OrdinalIgnoreCase)
        ? Principal["OXYDRIVER_FEAT_".Length..]
        : string.Empty;
    public string TableName
    {
        get
        {
            var t = Target ?? string.Empty;
            var idx = t.LastIndexOf('.');
            return idx >= 0 && idx + 1 < t.Length ? t[(idx + 1)..] : t;
        }
    }
}

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _run;
    private bool _running;

    public AsyncRelayCommand(Func<Task> run) => _run = run;

    public bool CanExecute(object? parameter) => !_running;
    public event EventHandler? CanExecuteChanged;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _run(); }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool> _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute ?? (_ => true);
    }

    public bool CanExecute(object? parameter) => _canExecute(parameter);
    public event EventHandler? CanExecuteChanged;
    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class FolderEntry : INotifyPropertyChanged
{
    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal))
                return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

