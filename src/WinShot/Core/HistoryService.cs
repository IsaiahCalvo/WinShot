using System.Drawing;
using System.Drawing.Imaging;
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
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Error($"Age prune failed for {file}", ex);
            }
        }
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
