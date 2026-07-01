using Microsoft.Win32;

namespace WinShot.Core;

/// <summary>
/// Registers the winshot:// URL protocol under HKCU (no elevation required).
/// The shell launches "WinShot.exe winshot://command"; CommandServer routes it.
/// </summary>
public static class ProtocolRegistrar
{
    private const string ClassesKeyPath = @"Software\Classes\winshot";

    /// <summary>Idempotent: re-registers only when the command no longer points at this exe.</summary>
    public static void EnsureRegistered()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Log.Error("Cannot register winshot:// protocol: process path is unknown");
                return;
            }

            string command = $"\"{exe}\" \"%1\"";

            // Fast path: already registered for this exe location.
            using (var existing = Registry.CurrentUser.OpenSubKey(ClassesKeyPath + @"\shell\open\command"))
            {
                if (existing?.GetValue(null) as string == command)
                    return;
            }

            using var root = Registry.CurrentUser.CreateSubKey(ClassesKeyPath);
            root.SetValue(null, "URL:WinShot Protocol");
            root.SetValue("URL Protocol", "");

            using (var icon = root.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");

            using (var cmd = root.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, command);

            Log.Info($"Registered winshot:// protocol handler -> {exe}");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to register winshot:// protocol", ex);
        }
    }
}
