using System.Diagnostics;
using Microsoft.Win32;

namespace WinShot.Core;

/// <summary>
/// Owns the "launch at Windows startup" entry. Two mechanisms exist: the installer
/// registers a logon Scheduled Task ("WinShot Autostart", durable against Run-key
/// throttling), while portable/dev installs use the HKCU Run key. Only one may be
/// active — both firing at logon launches WinShot twice, and the loser pops the
/// "already running" box. Centralized so the Settings window and the boot-time
/// self-heal agree on which mechanism owns autostart.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinShot";
    private const string AutostartTaskName = "WinShot Autostart";

    /// <summary>The exact command Windows should run at login: the quoted current exe path.</summary>
    public static string? LaunchCommand =>
        Environment.ProcessPath is { } exe ? $"\"{exe}\"" : null;

    /// <summary>
    /// Applies the Settings toggle. Enabled: the installer's Scheduled Task keeps
    /// ownership when present (Run key cleared), otherwise the Run key is written
    /// with the current exe path. Disabled: both mechanisms are removed.
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            bool taskOwnsAutostart = AutostartTaskExists();
            if (!enabled && taskOwnsAutostart)
                DeleteAutostartTask();

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            string? command = LaunchCommand;
            if (enabled && !taskOwnsAutostart && command is not null)
                key.SetValue(RunValueName, command);
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update launch-at-startup registration", ex);
        }
    }

    /// <summary>
    /// Reconciles autostart at app startup: when the Scheduled Task exists it owns
    /// autostart, so a duplicate Run entry is deleted (heals the double-launch);
    /// otherwise the Run key is rewritten only if it drifted from the current exe
    /// path. When disabled, does nothing (the user may have removed startup
    /// elsewhere on purpose).
    /// </summary>
    public static void Reconcile(bool enabled)
    {
        if (!enabled) return;

        try
        {
            string? command = LaunchCommand;
            if (command is null) return;

            if (AutostartTaskExists())
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (runKey?.GetValue(RunValueName) is not null)
                {
                    runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
                    Log.Info("Startup reconcile: removed duplicate Run entry — the Scheduled Task owns autostart.");
                }
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            string? current = key.GetValue(RunValueName) as string;
            if (!string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(RunValueName, command);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to reconcile launch-at-startup registration", ex);
        }
    }

    private static bool AutostartTaskExists() =>
        RunSchtasks($"/query /tn \"{AutostartTaskName}\"");

    private static void DeleteAutostartTask()
    {
        if (!RunSchtasks($"/delete /tn \"{AutostartTaskName}\" /f"))
            Log.Error("Failed to delete the autostart Scheduled Task", null);
    }

    /// <summary>Runs schtasks.exe hidden; true on exit code 0. Never throws.</summary>
    private static bool RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error("schtasks invocation failed", ex);
            return false;
        }
    }
}
