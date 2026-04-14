using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;

namespace Oxydriver.Ui;

public sealed class TrayController : IDisposable
{
    private readonly Window _mainWindow;
    private readonly Action _shutdown;
    private readonly Func<bool> _ensureUnlocked;
    private NotifyIcon? _notifyIcon;

    public TrayController(Window mainWindow, Action shutdown, Func<bool> ensureUnlocked)
    {
        _mainWindow = mainWindow;
        _shutdown = shutdown;
        _ensureUnlocked = ensureUnlocked;
    }

    public void Start()
    {
        if (_notifyIcon is not null) return;

        _notifyIcon = new NotifyIcon
        {
            Text = "OXYDRIVER",
            Visible = true,
            Icon = SystemIcons.Application,
            ContextMenuStrip = BuildMenu()
        };

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var statusText = "Statut: inconnu";
        if (_mainWindow.DataContext is MainWindowViewModel vmForStatus)
            statusText = $"Statut: {vmForStatus.HeaderIndicatorText}";
        var statusItem = new ToolStripMenuItem(statusText) { Enabled = false };
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ouvrir", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Redémarrer OXYDRIVER", null, (_, _) => RestartApplication());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Synchroniser avec l'API", null, (_, _) => ExecuteVmCommand(vm => vm.SyncCommand));
        menu.Items.Add("Lancer CloudFlare", null, (_, _) => ExecuteVmCommand(vm => vm.StartTunnelCommand));
        menu.Items.Add("Tester la connexion SQL", null, (_, _) => ExecuteVmCommand(vm => vm.TestSqlCommand));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => _shutdown());
        return menu;
    }

    private void ExecuteVmCommand(Func<MainWindowViewModel, ICommand> getCommand)
    {
        _mainWindow.Dispatcher.Invoke(() =>
        {
            if (_mainWindow.DataContext is not MainWindowViewModel vm) return;
            var cmd = getCommand(vm);
            if (cmd.CanExecute(null))
                cmd.Execute(null);
        });
    }

    private void RestartApplication()
    {
        _mainWindow.Dispatcher.Invoke(() =>
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true
                    });
                }
            }
            finally
            {
                _shutdown();
            }
        });
    }

    private void ShowMainWindow()
    {
        _mainWindow.Dispatcher.Invoke(() =>
        {
            if (!_ensureUnlocked())
                return;
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    public void Dispose()
    {
        if (_notifyIcon is null) return;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }
}

