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

    public HistoryService(SettingsService settings) => _settings = settings;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShot", "History");

    public string Add(Bitmap bmp)
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        bmp.Save(path, ImageFormat.Png);
        Prune();
        return path;
    }

    /// <summary>Copies an existing file (e.g. a finished recording) into history.</summary>
    public string AddFile(string sourcePath)
    {
        Directory.CreateDirectory(Dir);
        string ext = Path.GetExtension(sourcePath);
        string path = Path.Combine(Dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}{ext}");
        File.Copy(sourcePath, path);
        Prune();
        return path;
    }

    /// <summary>All history items, newest first.</summary>
    public List<string> GetItems()
    {
        if (!Directory.Exists(Dir)) return new List<string>();
        return Directory.GetFiles(Dir).OrderByDescending(f => f, StringComparer.Ordinal).ToList();
    }

    public void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log.Error($"Failed to delete history item {path}", ex); }
    }

    private void Prune()
    {
        var items = GetItems();
        foreach (string stale in items.Skip(Math.Max(1, _settings.Current.HistoryLimit)))
            Delete(stale);
    }
}
