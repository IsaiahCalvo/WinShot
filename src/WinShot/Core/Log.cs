using System.IO;

namespace WinShot.Core;

public static class Log
{
    private static readonly object Gate = new();

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShot", "logs");

    private static string FilePath => Path.Combine(Dir, "winshot.log");

    public static void Info(string message) => WriteLine("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        WriteLine("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void WriteLine(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
