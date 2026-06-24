using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace WinShot.Core;

/// <summary>
/// Persists every capture to a local history folder and prunes old items
/// past the configured limit. File names sort chronologically.
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

    public string Add(Bitmap bmp)
    {
        lock (_gate)
        {
            string dir = _directoryProvider();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            bmp.Save(path, ImageFormat.Png);
            PruneCore();
            PruneByAgeCore(_settings.Current.HistoryRetentionDays);
            return path;
        }
    }

    /// <summary>Copies an existing file (e.g. a finished recording) into history.</summary>
    public string AddFile(string sourcePath)
    {
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

    /// <summary>All history items, newest first.</summary>
    public List<string> GetItems()
    {
        lock (_gate)
            return GetItemsCore();
    }

    public void Delete(string path)
    {
        lock (_gate)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log.Error($"Failed to delete history item {path}", ex); }
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
        return Directory.GetFiles(dir).OrderByDescending(f => f, StringComparer.Ordinal).ToList();
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
