using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace WinShot.Core;

/// <summary>
/// Persists every capture to a local history folder and prunes old items
/// past the configured limit. Everything is ordered by capture time, never by name —
/// a renamed capture has no timestamp prefix left to sort on.
/// </summary>
public class HistoryService
{
    private readonly SettingsService _settings;
    private readonly Func<string> _directoryProvider;
    private readonly object _gate = new();

    public HistoryService(SettingsService settings, Func<string>? directoryProvider = null)
    {
        _settings = settings;
        _directoryProvider = directoryProvider ?? (() => Dir);
    }

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShot", "History");

    /// <summary>
    /// Writes a capture into history, or returns null when history is switched off.
    /// <paramref name="sourceApp"/> is the process the capture came from; it rides along in
    /// the file name, which is the only metadata channel this flat folder has.
    /// </summary>
    public string? Add(Bitmap bmp, HistoryCaptureKind kind = HistoryCaptureKind.Regular, string? sourceApp = null)
    {
        if (!_settings.Current.HistoryEnabled) return null;
        lock (_gate)
        {
            string dir = _directoryProvider();
            Directory.CreateDirectory(dir);
            string suffix = kind == HistoryCaptureKind.Scrolling ? "-scroll" : string.Empty;
            string path = Path.Combine(
                dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}{suffix}{SourceAppSuffix(sourceApp)}.png");
            bmp.Save(path, ImageFormat.Png);
            PruneCore();
            PruneByAgeCore(_settings.Current.HistoryRetentionDays);
            return path;
        }
    }

    /// <summary>Copies an existing file (e.g. a finished recording) into history,
    /// or returns null when history is switched off.</summary>
    public string? AddFile(string sourcePath)
    {
        if (!_settings.Current.HistoryEnabled) return null;
        lock (_gate)
        {
            string dir = _directoryProvider();
            Directory.CreateDirectory(dir);
            string ext = Path.GetExtension(sourcePath);
            string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}{ext}");
            File.Copy(sourcePath, path);
            PruneCore();
            PruneByAgeCore(_settings.Current.HistoryRetentionDays);
            return path;
        }
    }

    /// <summary>
    /// "@chrome" for a capture taken over Chrome, empty when the owning app is unknown.
    /// Everything Windows disallows in a name — and '@' itself, so the last '@' always
    /// starts the tag — is dropped rather than replaced, since this is a label, not a name
    /// the user typed. WinShot's own windows are not a source worth badging.
    /// </summary>
    internal static string SourceAppSuffix(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        // Dots survive: dropping them turned "Microsoft.Photos" into "MicrosoftPhotos", which
        // never matches the live process again, so those captures could never wear a badge. Only
        // a trailing one has to go — Windows will not keep a file name that ends with a dot.
        string cleaned = new string(app.Trim()
                .Where(c => c != '@' && !invalid.Contains(c))
                .Take(32)
                .ToArray())
            .TrimEnd('.');
        if (cleaned.Length == 0 ||
            cleaned.Equals("Desktop", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("WinShot", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return "@" + cleaned;
    }

    /// <summary>All history items, newest first.</summary>
    public List<string> GetItems()
    {
        lock (_gate)
            return GetItemsCore();
    }

    /// <summary>
    /// Deletes one history copy. Success is reported only after the file no longer exists,
    /// so callers do not remove the visible tile when Windows rejected the disk operation.
    /// </summary>
    public HistoryDeleteResult Delete(string path)
    {
        lock (_gate)
        {
            try
            {
                File.Delete(path);
                if (!File.Exists(path))
                    return HistoryDeleteResult.Success;

                const string stillExists = "Windows reported that the file was deleted, but it is still on disk.";
                Log.Error($"Failed to delete history item {path}: {stillExists}");
                return HistoryDeleteResult.Failure(stillExists);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to delete history item {path}", ex);
                // HistoryWindow shows this verbatim in a "Delete failed" dialog.
                return HistoryDeleteResult.Failure(FriendlyError.For(ex));
            }
        }
    }

    /// <summary>Deletes history files older than <paramref name="days"/> days.
    /// 0 (or negative) is a no-op so "keep forever" stays the default.</summary>
    public void PruneByAge(int days)
    {
        if (days <= 0) return;
        lock (_gate)
            PruneByAgeCore(days);
    }

    private void PruneByAgeCore(int days)
    {
        if (days <= 0) return;

        DateTime cutoff = DateTime.Now.AddDays(-days);
        foreach (string file in GetItemsCore())
        {
            try
            {
                if (FileTimestamp(file) < cutoff)
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Error($"Age prune failed for {file}", ex);
            }
        }
    }

    /// <summary>
    /// Age decisions use the capture time encoded in the file NAME (what the UI sorts by),
    /// not the file's last-write time. Copied recordings or externally touched files can have
    /// a write time that diverges from the capture time, which would otherwise prune by a
    /// different clock than the user sees. Falls back to write time when unparseable.
    /// </summary>
    private static DateTime FileTimestamp(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        if (name.Length >= 19 &&
            DateTime.TryParseExact(name[..19], "yyyyMMdd-HHmmss-fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime ts))
        {
            return ts;
        }

        try { return File.GetLastWriteTime(file); }
        catch { return DateTime.Now; }
    }

    private List<string> GetItemsCore()
    {
        string dir = _directoryProvider();
        if (!Directory.Exists(dir)) return new List<string>();
        // Newest first by CAPTURE TIME. Ordering by name was the same thing until rename
        // shipped: a renamed file drops its timestamp prefix, so "1099 tax form.png" sorts
        // below every "2026…" name and PruneCore would delete it on the very next capture,
        // however recent it is. Name order is only the tie-break now.
        return Directory.GetFiles(dir)
            .OrderByDescending(FileTimestamp)
            .ThenByDescending(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private void PruneCore()
    {
        var items = GetItemsCore();
        foreach (string stale in items.Skip(Math.Max(1, _settings.Current.HistoryLimit)))
        {
            try { File.Delete(stale); }
            catch (Exception ex) { Log.Error($"Failed to delete history item {stale}", ex); }
        }
    }
}

public enum HistoryCaptureKind
{
    Regular,
    Scrolling,
}

public readonly record struct HistoryDeleteResult(bool Succeeded, string? ErrorMessage)
{
    public static HistoryDeleteResult Success => new(true, null);

    public static HistoryDeleteResult Failure(string message) =>
        new(false, string.IsNullOrWhiteSpace(message) ? "The history item could not be deleted." : message);
}
