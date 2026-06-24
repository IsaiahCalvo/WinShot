using Microsoft.Win32;

namespace WinShot.Core;

/// <summary>
/// Owns the "launch at Windows startup" Run-key entry. Centralized so the Settings window
/// and the boot-time self-heal write the exact same value, and so a moved/updated exe is
/// reconciled at startup instead of leaving a stale Run entry that silently fails to launch.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinShot";

    /// <summary>The exact command Windows should run at login: the quoted current exe path.</summary>
    public static string? LaunchCommand =>
        Environment.ProcessPath is { } exe ? $"\"{exe}\"" : null;

    /// <summary>
    /// Writes (enabled) or removes (disabled) the Run-key entry. When enabled it always writes
    /// the CURRENT exe path, so calling it also self-heals a stale/moved entry.
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            string? command = LaunchCommand;
            if (enabled && command is not null)
                key.SetValue(RunValueName, command);
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update launch-at-startup registry value", ex);
        }
    }

    /// <summary>
    /// Reconciles the Run-key entry at startup: when enabled, rewrites it only if it drifted from
    /// the current exe path (so we don't write the registry on every launch); when disabled, does
    /// nothing (the user may have removed startup elsewhere on purpose).
    /// </summary>
    public static void Reconcile(bool enabled)
    {
        if (!enabled) return;

        try
        {
            string? command = LaunchCommand;
            if (command is null) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            string? current = key.GetValue(RunValueName) as string;
            if (!string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(RunValueName, command);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to reconcile launch-at-startup registry value", ex);
        }
    }
}
