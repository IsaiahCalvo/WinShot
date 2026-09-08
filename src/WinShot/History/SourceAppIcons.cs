using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.History;

/// <summary>
/// Turns the process name stamped into a history file name ("…@chrome.png") into the app's
/// icon for the tile badge.
///
/// The file name is the whole metadata channel, so the executable has to be found again from
/// the process name. A copy of that app still running is the only reliable way to do that, so
/// a capture from an app the user has since quit simply gets no badge — the same outcome as a
/// capture with no owning app at all, which is what CleanShot shows too.
///
/// Hits are cached per process name; misses deliberately are not. Caching a miss made the badge
/// depend on what happened to be running the first time History opened: open it with Chrome
/// closed, start Chrome, refresh, and every "@chrome" tile stayed unbadged for the rest of the
/// WinShot session. A miss costs one Process.GetProcessesByName, it runs off the UI thread after
/// the tiles are already up, and it only runs for tiles that carry a tag at all.
/// </summary>
internal static class SourceAppIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Frozen icon for a process name, or null when it cannot be resolved.
    /// Safe to call from any thread; the returned image is frozen, so it can cross to the UI.</summary>
    public static ImageSource? For(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(processName, out ImageSource? cached))
                return cached;
        }

        ImageSource? icon = Load(processName);
        if (icon is not null)
        {
            lock (Cache)
                Cache[processName] = icon;
        }

        return icon;
    }

    internal static void ResetCacheForTests()
    {
        lock (Cache)
            Cache.Clear();
    }

    private static ImageSource? Load(string processName)
    {
        string? executable = FindExecutable(processName);
        if (executable is null)
            return null;

        try
        {
            using SD.Icon? icon = SD.Icon.ExtractAssociatedIcon(executable);
            if (icon is null)
                return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read the icon for {executable}", ex);
            return null;
        }
    }

    private static string? FindExecutable(string processName)
    {
        Process[] running;
        try
        {
            running = Process.GetProcessesByName(processName);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not look up the process {processName}", ex);
            return null;
        }

        try
        {
            foreach (Process process in running)
            {
                // MainModule throws for a process of the other bitness or a higher integrity
                // level; try the next copy rather than giving up on the app.
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
                catch
                {
                }
            }
        }
        finally
        {
            foreach (Process process in running)
                process.Dispose();
        }

        return null;
    }
}
