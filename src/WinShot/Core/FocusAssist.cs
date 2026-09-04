using Microsoft.Win32;

namespace WinShot.Core;

/// <summary>
/// Silences Windows notification banners for the length of a recording and puts them back
/// exactly as they were, so a toast never lands in the middle of a take.
///
/// Windows exposes no supported API for Focus Assist, but the shell reads the two per-user
/// notification switches live, so writing them is the same thing the Settings toggle does.
/// Both are written, because Windows 10 and Windows 11 read different ones.
///
/// Never throws: a locked-down machine that refuses the write simply records with
/// notifications on, which is the behaviour before this existed.
/// </summary>
public static class FocusAssist
{
    private const string PushNotificationsKey =
        @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string NotificationSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    /// <summary>The switches to hold down, as (registry key, value name).</summary>
    internal static readonly IReadOnlyList<(string Key, string Value)> Switches =
    [
        (PushNotificationsKey, "ToastEnabled"),
        (NotificationSettingsKey, "NOC_GLOBAL_SETTING_TOASTS_ENABLED"),
    ];

    private static readonly Session Live = new(ReadRegistry, WriteRegistry);

    /// <summary>True while this process is holding notifications down.</summary>
    public static bool Active => Live.Active;

    public static void Suppress() => Live.Suppress();

    public static void Restore() => Live.Restore();

    /// <summary>
    /// The save/restore state machine, with the store injected so the ordering rules — a
    /// second Suppress must not overwrite the saved values with the ones this class just
    /// wrote, and a Restore with nothing saved must not invent a value — are testable
    /// without touching the real registry.
    /// </summary>
    internal sealed class Session(Func<string, string, int?> read, Action<string, string, int?> write)
    {
        private Dictionary<(string Key, string Value), int?>? _saved;

        public bool Active => _saved is not null;

        public void Suppress()
        {
            if (_saved is not null)
                return; // already holding; the first Suppress owns the saved state

            var saved = new Dictionary<(string, string), int?>();
            foreach ((string key, string value) in Switches)
            {
                saved[(key, value)] = read(key, value);
                write(key, value, 0);
            }
            _saved = saved;
        }

        public void Restore()
        {
            Dictionary<(string Key, string Value), int?>? saved = _saved;
            _saved = null;
            if (saved is null)
                return;

            foreach (KeyValuePair<(string Key, string Value), int?> entry in saved)
                write(entry.Key.Key, entry.Key.Value, entry.Value);
        }
    }

    private static int? ReadRegistry(string key, string value)
    {
        try
        {
            using RegistryKey? subKey = Registry.CurrentUser.OpenSubKey(key);
            return subKey?.GetValue(value) as int?;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read the notification switch before recording", ex);
            return null;
        }
    }

    /// <summary>A null value means "there was nothing here" — the value is removed rather
    /// than written back as a zero the user never chose.</summary>
    private static void WriteRegistry(string key, string value, int? data)
    {
        try
        {
            using RegistryKey subKey = Registry.CurrentUser.CreateSubKey(key);
            if (data is int number)
                subKey.SetValue(value, number, RegistryValueKind.DWord);
            else
                subKey.DeleteValue(value, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to change the notification switch for recording", ex);
        }
    }
}
