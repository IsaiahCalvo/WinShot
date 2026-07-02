using System.Collections.Concurrent;
using System.IO;

namespace WinShot.Core;

public static class Log
{
    private static readonly object Gate = new();
    private static readonly BlockingCollection<string> InfoQueue = new();

    static Log()
    {
        var thread = new Thread(DrainInfoQueue)
        {
            IsBackground = true,
            Name = "WinShot Log Writer",
        };
        thread.Start();
    }

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShot", "logs");

    private static string FilePath => Path.Combine(Dir, "winshot.log");

    public static void Info(string message)
    {
        try
        {
            InfoQueue.Add(FormatLine("INFO", message));
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Error(string message, Exception? ex = null) =>
        WriteLine("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void DrainInfoQueue()
    {
        foreach (string line in InfoQueue.GetConsumingEnumerable())
            WriteFormattedLine(line);
    }

    private static string FormatLine(string level, string message) =>
        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";

    private static void WriteLine(string level, string message)
        => WriteFormattedLine(FormatLine(level, message));

    private static void WriteFormattedLine(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, line);
            }
        }
        catch
        {
            // Main log unwritable (locked/permissions): fall back to a per-process file so a
            // broken instance is at least diagnosable instead of silently logless.
            try
            {
                File.AppendAllText(Path.Combine(Dir, $"winshot-{Environment.ProcessId}.log"), line);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
