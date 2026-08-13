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

    private const string OpenWithProgId = "WinShot.Image";
    private static readonly string[] OpenWithExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"];

    /// <summary>
    /// Registers WinShot in Explorer's "Open with" list for image files (HKCU, no
    /// elevation): a WinShot.Image ProgID whose open command launches the editor, plus
    /// an OpenWithProgids entry per image extension. Idempotent like EnsureRegistered.
    /// </summary>
    public static void EnsureOpenWithRegistered()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;

            string command = $"\"{exe}\" \"%1\"";

            using (var existing = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{OpenWithProgId}\shell\open\command"))
            {
                if (existing?.GetValue(null) as string == command)
                    return;
            }

            using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{OpenWithProgId}"))
            {
                progId.SetValue(null, "WinShot Image");
                using (var icon = progId.CreateSubKey("DefaultIcon"))
                    icon.SetValue(null, $"\"{exe}\",0");
                using (var cmd = progId.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue(null, command);
            }

            foreach (string ext in OpenWithExtensions)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
                key.SetValue(OpenWithProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            Log.Info($"Registered Open With handler for images -> {exe}");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to register Open With handler", ex);
        }
    }
}
