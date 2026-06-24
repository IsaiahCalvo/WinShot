using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinShot.Core;

/// <summary>
/// Expands the user's file-name template (Settings.FileNameTemplate) into a concrete
/// file name. Supported tokens: {date} {time} {n} {app} {title}.
/// </summary>
public static class FileNamer
{
    private const string DefaultTemplate = "WinShot {date} at {time}";
    private const int MaxTokenLength = 60;

    /// <summary>
    /// Returns the next templated file name including the dot and extension,
    /// e.g. "WinShot 2026-06-12 at 14.03.22.png". If the template contains {n},
    /// Settings.NextCounter is post-incremented in memory (the app persists settings
    /// on exit; this method never calls Save).
    /// </summary>
    public static string Next(SettingsService settings, string extension)
    {
        string template = settings.Current.FileNameTemplate;
        if (string.IsNullOrWhiteSpace(template))
            template = DefaultTemplate;

        var now = DateTime.Now;
        string name = Sanitize(Expand(template, now, settings));

        // A template that produced nothing usable (empty or no letters/digits) is garbage.
        if (string.IsNullOrWhiteSpace(name) || !name.Any(char.IsLetterOrDigit))
            name = Sanitize(Expand(DefaultTemplate, now, settings));

        string ext = extension.TrimStart('.');
        return $"{name}.{ext}";
    }

    public static string NextUniquePath(SettingsService settings, string directory, string extension)
    {
        string fileName = Next(settings, extension);
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            return path;

        string name = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int suffix = 2; suffix < 10_000; suffix++)
        {
            path = Path.Combine(directory, $"{name} {suffix}{ext}");
            if (!File.Exists(path))
                return path;
        }

        return Path.Combine(directory, $"{name} {Guid.NewGuid():N}{ext}");
    }

    private static string Expand(string template, DateTime now, SettingsService settings)
    {
        string result = template;

        if (result.Contains("{date}", StringComparison.OrdinalIgnoreCase))
            result = result.Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase);

        if (result.Contains("{time}", StringComparison.OrdinalIgnoreCase))
            result = result.Replace("{time}", now.ToString("HH.mm.ss"), StringComparison.OrdinalIgnoreCase);

        if (result.Contains("{n}", StringComparison.OrdinalIgnoreCase))
        {
            int n = settings.Current.NextCounter;
            settings.Current.NextCounter = n + 1; // persisted by the app on exit
            result = result.Replace("{n}", n.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        if (result.Contains("{app}", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("{title}", StringComparison.OrdinalIgnoreCase))
        {
            var (app, title) = GetForegroundInfo();
            result = result.Replace("{app}", app, StringComparison.OrdinalIgnoreCase)
                           .Replace("{title}", title, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static (string App, string Title) GetForegroundInfo()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return ("Desktop", "Desktop");

            string app = "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    app = proc.ProcessName;
                }
                catch
                {
                    // Process may have exited or be inaccessible; leave app empty.
                }
            }

            string title = "";
            int length = GetWindowTextLength(hwnd);
            if (length > 0)
            {
                var sb = new StringBuilder(length + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            app = Truncate(Sanitize(app));
            title = Truncate(Sanitize(title));
            if (app.Length == 0) app = "Desktop";
            if (title.Length == 0) title = app;
            return (app, title);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read foreground window for file name", ex);
            return ("Desktop", "Desktop");
        }
    }

    /// <summary>Replaces invalid file-name characters with '-' and trims trailing dots/spaces.</summary>
    private static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);
        return sb.ToString().Trim().TrimEnd('.', ' ');
    }

    private static string Truncate(string value) =>
        value.Length <= MaxTokenLength ? value : value[..MaxTokenLength].TrimEnd('.', ' ');

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
