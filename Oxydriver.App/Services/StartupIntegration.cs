using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Oxydriver.Services;

public sealed class StartupIntegration
{
    private const string AppName = "OXYDRIVER";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public Task ApplyAsync(bool enable)
    {
        // Startup per-user: fiable et ne requiert pas d'élévation admin.
        var exe = Process.GetCurrentProcess().MainModule?.FileName
                  ?? throw new InvalidOperationException("Chemin exécutable introuvable.");

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Impossible d'ouvrir la clé de registre Run.");

        if (enable)
        {
            key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
        return Task.CompletedTask;
    }
}

