using System.IO;
using System.Text.Json;
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
///
/// The saved values are also written to disk before the switches are touched, because the
/// in-memory copy dies with the process: a crash or a Task Manager kill mid-take would
/// otherwise leave the user's notifications off for good, with nothing in the app to put them
/// back. <see cref="RecoverFromCrash"/> at startup finishes the job the dead process could not.
/// </summary>
public static class FocusAssist
{
    /// <summary>The breadcrumb, beside settings.json so it follows the same profile.</summary>
    private static string BreadcrumbPath => Path.Combine(SettingsService.Dir, "focus-assist.json");

    private sealed record SavedSwitch(string Key, string Value, int? Data);
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

    private static readonly Session Live = new(ReadRegistry, WriteRegistry, SaveBreadcrumb, ClearBreadcrumb);

    /// <summary>True while this process is holding notifications down.</summary>
    public static bool Active => Live.Active;

    public static void Suppress() => Live.Suppress();

    public static void Restore() => Live.Restore();

    /// <summary>
    /// Puts the switches back after a crash or a kill during a take, then drops the
    /// breadcrumb. Safe to call on every launch: with no breadcrumb it does nothing.
    /// Returns true when it actually restored something, for the log.
    /// </summary>
    public static bool RecoverFromCrash()
    {
        try
        {
            if (!File.Exists(BreadcrumbPath))
                return false;

            SavedSwitch[]? saved = JsonSerializer.Deserialize<SavedSwitch[]>(
                File.ReadAllText(BreadcrumbPath));
            File.Delete(BreadcrumbPath);
            if (saved is null || saved.Length == 0)
                return false;

            foreach (SavedSwitch entry in saved)
                WriteRegistry(entry.Key, entry.Value, entry.Data);
            Log.Info("Notification banners restored after a recording that never finished");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore the notification switches after a crash", ex);
            return false;
        }
    }

    private static void SaveBreadcrumb(IReadOnlyList<(string Key, string Value, int? Data)> saved)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.Dir);
            File.WriteAllText(
                BreadcrumbPath,
                JsonSerializer.Serialize(
                    saved.Select(s => new SavedSwitch(s.Key, s.Value, s.Data)).ToArray()));
        }
        catch (Exception ex)
        {
            // Best effort: without the breadcrumb the in-process restore still works, which is
            // exactly the behaviour before this existed.
            Log.Error("Failed to record the notification switches before silencing them", ex);
        }
    }

    private static void ClearBreadcrumb()
    {
        try
        {
            if (File.Exists(BreadcrumbPath))
                File.Delete(BreadcrumbPath);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to clear the notification-switch breadcrumb", ex);
        }
    }

    /// <summary>
    /// The save/restore state machine, with the store injected so the ordering rules — a
    /// second Suppress must not overwrite the saved values with the ones this class just
    /// wrote, and a Restore with nothing saved must not invent a value — are testable
    /// without touching the real registry.
    /// </summary>
    internal sealed class Session(
        Func<string, string, int?> read,
        Action<string, string, int?> write,
        Action<IReadOnlyList<(string Key, string Value, int? Data)>>? persist = null,
        Action? clearPersisted = null)
    {
        private Dictionary<(string Key, string Value), int?>? _saved;

        public bool Active => _saved is not null;

        public void Suppress()
        {
            if (_saved is not null)
                return; // already holding; the first Suppress owns the saved state

            var saved = new Dictionary<(string, string), int?>();
            var breadcrumb = new List<(string, string, int?)>(Switches.Count);
            foreach ((string key, string value) in Switches)
            {
                int? previous = read(key, value);
                saved[(key, value)] = previous;
                breadcrumb.Add((key, value, previous));
            }
            // On disk BEFORE the switches move: a crash between the two writes is recoverable,
            // a crash after a write we never recorded is not.
            persist?.Invoke(breadcrumb);
            foreach ((string key, string value) in Switches)
                write(key, value, 0);
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
            clearPersisted?.Invoke();
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
