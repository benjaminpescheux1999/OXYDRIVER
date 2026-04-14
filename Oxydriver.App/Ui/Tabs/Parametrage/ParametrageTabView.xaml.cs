using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Oxydriver.Services;
using Oxydriver.Ui;

namespace Oxydriver.Ui.Tabs.Parametrage;

public partial class ParametrageTabView : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private bool _isLoadingDraft;
    private string _oldUiPasswordAttempt = string.Empty;
    private AppSettings _baselineSettings = new();
    private bool _hasDraftChanges;
    private static readonly System.Windows.Media.Brush DirtyBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
    private static readonly System.Windows.Media.Brush DirtyTextBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 83, 9));
    private static readonly System.Windows.Media.Brush DefaultTextBrush = System.Windows.Media.Brushes.Black;

    // Brouillon éditable: le formulaire écrit ici, pas directement dans Settings.
    public AppSettings DraftSettings { get; private set; } = new();
    public bool HasDraftChanges
    {
        get => _hasDraftChanges;
        private set
        {
            if (_hasDraftChanges == value) return;
            _hasDraftChanges = value;
            OnPropertyChanged();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public ParametrageTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
#if !DEBUG
        SftpDebugGroup.Visibility = Visibility.Collapsed;
#endif
        ReloadDraftFromVm();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ReloadDraftFromVm();
    }

    private void ReloadDraftFromVm()
    {
        if (Vm is null) return;

        _isLoadingDraft = true;
        try
        {
            _baselineSettings = CloneSettings(Vm.Settings);
            DraftSettings = CloneSettings(_baselineSettings);
            OnPropertyChanged(nameof(DraftSettings));
            _oldUiPasswordAttempt = string.Empty;
            OldUiPasswordBox.Password = string.Empty;
            AccessKeyPasswordBox.Password = DraftSettings.AccessKey ?? string.Empty;
            UiPasswordBox.Password = DraftSettings.UiPassword ?? string.Empty;
            SqlPasswordBox.Password = DraftSettings.SqlPassword ?? string.Empty;
            SftpPasswordBox.Password = DraftSettings.SftpPassword ?? string.Empty;
            RefreshDraftChangeState();
        }
        finally
        {
            _isLoadingDraft = false;
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
            BackupEncryptionKey = source.BackupEncryptionKey,
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

    private async void SaveFormClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var uiPasswordChanged = HasChanged(DraftSettings.UiPassword, _baselineSettings.UiPassword);
        if (uiPasswordChanged && !string.Equals(_oldUiPasswordAttempt, _baselineSettings.UiPassword, StringComparison.Ordinal))
        {
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                "Ancien mot de passe interface invalide.",
                "Sécurité",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            OldUiPasswordBox.Focus();
            OldUiPasswordBox.SelectAll();
            return;
        }
        await Vm.SaveSettingsFromFormAsync(CloneSettings(DraftSettings));
        ReloadDraftFromVm();
    }

    private void CancelFormClicked(object sender, RoutedEventArgs e)
    {
        ReloadDraftFromVm();
    }

    private async void ExportBackupClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter backup OXYDRIVER",
            Filter = "Backup chiffré (*.oxybak)|*.oxybak|JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*",
            FileName = $"oxydriver-backup-{DateTime.Now:yyyyMMdd-HHmmss}.oxybak"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await Vm.ExportBackupAsync(dialog.FileName);
    }

    private async void ImportBackupClicked(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importer backup OXYDRIVER",
            Filter = "Backup chiffré (*.oxybak)|*.oxybak|JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var confirm = new ConfirmUiPasswordWindow();
        if (confirm.ShowDialog() != true)
            return;

        await Vm.ImportBackupAsync(dialog.FileName, confirm.EnteredPassword);
    }

    private void AccessKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDraft) return;
        if (sender is PasswordBox pb)
            DraftSettings.AccessKey = pb.Password;
        RefreshDraftChangeState();
    }

    private void SqlPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDraft) return;
        if (sender is PasswordBox pb)
            DraftSettings.SqlPassword = pb.Password;
        RefreshDraftChangeState();
    }

    private void UiPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDraft) return;
        if (sender is PasswordBox pb)
            DraftSettings.UiPassword = pb.Password;
        RefreshDraftChangeState();
    }

    private void OldUiPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDraft) return;
        if (sender is PasswordBox pb)
            _oldUiPasswordAttempt = pb.Password;
    }

    private void SftpPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDraft) return;
        if (sender is PasswordBox pb)
            DraftSettings.SftpPassword = pb.Password;
        RefreshDraftChangeState();
    }

    private void DraftValueChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingDraft) return;
        RefreshDraftChangeState();
    }

    private void DraftSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDraft) return;
        RefreshDraftChangeState();
    }

    private void DraftCheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDraft) return;
        RefreshDraftChangeState();
    }

    /// <summary>
    /// Mets en évidence uniquement les champs réellement modifiés dans le formulaire.
    /// </summary>
    private void RefreshDraftChangeState()
    {
        var apiBaseChanged = HasChanged(DraftSettings.ApiBaseUrl, _baselineSettings.ApiBaseUrl);
        var accessKeyChanged = HasChanged(DraftSettings.AccessKey, _baselineSettings.AccessKey);
        var uiPasswordChanged = HasChanged(DraftSettings.UiPassword, _baselineSettings.UiPassword);

        var sqlServerChanged = HasChanged(DraftSettings.SqlServerHost, _baselineSettings.SqlServerHost);
        var sqlAuthChanged = HasChanged(DraftSettings.SqlAuthenticationMode, _baselineSettings.SqlAuthenticationMode);
        var sqlUserChanged = HasChanged(DraftSettings.SqlUserName, _baselineSettings.SqlUserName);
        var sqlPwdChanged = HasChanged(DraftSettings.SqlPassword, _baselineSettings.SqlPassword);
        var sqlDbChanged = HasChanged(DraftSettings.DefaultDatabase, _baselineSettings.DefaultDatabase);
        var sqlEncryptChanged = HasChanged(DraftSettings.SqlEncryptMode, _baselineSettings.SqlEncryptMode);
        var sqlTrustChanged = DraftSettings.SqlTrustServerCertificate != _baselineSettings.SqlTrustServerCertificate;
        var sqlTimeoutChanged = HasChanged(DraftSettings.SqlConnectTimeoutSeconds, _baselineSettings.SqlConnectTimeoutSeconds);

        var portChanged = HasChanged(DraftSettings.LocalPort, _baselineSettings.LocalPort);
        var exposureModeChanged = HasChanged(DraftSettings.ExposureMode, _baselineSettings.ExposureMode);
        var manualTunnelChanged = HasChanged(DraftSettings.ManualTunnelUrl, _baselineSettings.ManualTunnelUrl);
        var exposureProviderChanged = HasChanged(DraftSettings.ExposureProvider, _baselineSettings.ExposureProvider);
        var startupChanged = DraftSettings.LaunchAtStartup != _baselineSettings.LaunchAtStartup;

        var sftpHostChanged = HasChanged(DraftSettings.SftpHost, _baselineSettings.SftpHost);
        var sftpPortChanged = HasChanged(DraftSettings.SftpPort, _baselineSettings.SftpPort);
        var sftpUserChanged = HasChanged(DraftSettings.SftpUsername, _baselineSettings.SftpUsername);
        var sftpPwdChanged = HasChanged(DraftSettings.SftpPassword, _baselineSettings.SftpPassword);
        var sftpPathChanged = HasChanged(DraftSettings.SftpRemotePath, _baselineSettings.SftpRemotePath);

        SetControlDirty(ApiBaseUrlTextBox, apiBaseChanged);
        SetControlDirty(AccessKeyPasswordBox, accessKeyChanged);
        SetControlDirty(UiPasswordBox, uiPasswordChanged);

        SetControlDirty(SqlServerHostTextBox, sqlServerChanged);
        SetControlDirty(SqlAuthComboBox, sqlAuthChanged);
        SetControlDirty(SqlUserTextBox, sqlUserChanged);
        SetControlDirty(SqlPasswordBox, sqlPwdChanged);
        SetControlDirty(DefaultDatabaseTextBox, sqlDbChanged);
        SetControlDirty(SqlEncryptComboBox, sqlEncryptChanged);
        SetToggleDirty(SqlTrustCheckBox, sqlTrustChanged);
        SetControlDirty(SqlTimeoutTextBox, sqlTimeoutChanged);

        SetControlDirty(LocalPortTextBox, portChanged);
        SetControlDirty(ExposureModeComboBox, exposureModeChanged);
        SetControlDirty(ManualTunnelUrlTextBox, manualTunnelChanged);
        SetControlDirty(ExposureProviderTextBox, exposureProviderChanged);
        SetToggleDirty(LaunchAtStartupCheckBox, startupChanged);

        SetControlDirty(SftpHostTextBox, sftpHostChanged);
        SetControlDirty(SftpPortTextBox, sftpPortChanged);
        SetControlDirty(SftpUsernameTextBox, sftpUserChanged);
        SetControlDirty(SftpPasswordBox, sftpPwdChanged);
        SetControlDirty(SftpRemotePathTextBox, sftpPathChanged);

        SetGroupDirty(ApiGroupBox, apiBaseChanged || accessKeyChanged || uiPasswordChanged);
        SetGroupDirty(SqlGroupBox, sqlServerChanged || sqlAuthChanged || sqlUserChanged || sqlPwdChanged || sqlDbChanged || sqlEncryptChanged || sqlTrustChanged || sqlTimeoutChanged);
        SetGroupDirty(TunnelGroupBox, portChanged || exposureModeChanged || manualTunnelChanged || exposureProviderChanged);
        SetGroupDirty(StartupGroupBox, startupChanged);
        SetGroupDirty(SftpDebugGroup, sftpHostChanged || sftpPortChanged || sftpUserChanged || sftpPwdChanged || sftpPathChanged);

        HasDraftChanges =
            apiBaseChanged || accessKeyChanged || uiPasswordChanged ||
            sqlServerChanged || sqlAuthChanged || sqlUserChanged || sqlPwdChanged || sqlDbChanged || sqlEncryptChanged || sqlTrustChanged || sqlTimeoutChanged ||
            portChanged || exposureModeChanged || manualTunnelChanged || exposureProviderChanged || startupChanged ||
            sftpHostChanged || sftpPortChanged || sftpUserChanged || sftpPwdChanged || sftpPathChanged;
    }

    private static bool HasChanged(string? current, string? baseline)
        => !string.Equals((current ?? string.Empty).Trim(), (baseline ?? string.Empty).Trim(), StringComparison.Ordinal);

    private static void SetControlDirty(System.Windows.Controls.Control? control, bool dirty)
    {
        if (control is null) return;
        control.BorderThickness = dirty ? new Thickness(2) : new Thickness(1);
        control.BorderBrush = dirty ? DirtyBrush : System.Windows.SystemColors.ControlDarkBrush;
    }

    private static void SetGroupDirty(System.Windows.Controls.GroupBox? groupBox, bool dirty)
    {
        if (groupBox is null) return;
        groupBox.BorderThickness = dirty ? new Thickness(2) : new Thickness(1);
        groupBox.BorderBrush = dirty ? DirtyBrush : System.Windows.SystemColors.ControlDarkBrush;
    }

    private static void SetToggleDirty(System.Windows.Controls.CheckBox? checkBox, bool dirty)
    {
        if (checkBox is null) return;
        checkBox.FontWeight = dirty ? FontWeights.SemiBold : FontWeights.Normal;
        checkBox.Foreground = dirty ? DirtyTextBrush : DefaultTextBrush;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
