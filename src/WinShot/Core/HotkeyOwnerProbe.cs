using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinShot.Core;

public static class HotkeyOwnerProbe
{
    public sealed record Observation(
        int ProcessId,
        string ProcessName,
        string? MainModulePath,
        string? WindowTitle);

    public sealed record Result(bool Found, HotkeyConflictSource Source, string Message);

    public static Result Evaluate(string gesture, Observation baseline, IEnumerable<Observation> observations)
    {
        foreach (var observation in observations)
        {
            if (observation.ProcessId <= 0 ||
                observation.ProcessId == baseline.ProcessId ||
                string.Equals(observation.ProcessName, baseline.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = HotkeyConflictInspector.DescribeProcessCandidate(
                new HotkeyConflictInspector.ProcessCandidate(
                    observation.ProcessName,
                    observation.MainModulePath));

            return new Result(
                true,
                source,
                $"{source.DisplayName} became active after {gesture} was pressed.");
        }

        return new Result(
            false,
            new HotkeyConflictSource(
                "another app on this PC",
                "ms-settings:keyboard",
                false,
                "WinShot could not identify the app that received the hotkey."),
            "No app became active while WinShot was watching.");
    }

    public static async Task<Result> RunAsync(string gesture, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Observation baseline = CaptureForeground();
        var observations = new List<Observation> { baseline };
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            Observation current = CaptureForeground();
            observations.Add(current);

            Result result = Evaluate(gesture, baseline, observations);
            if (result.Found)
                return result;
        }

        return Evaluate(gesture, baseline, observations);
    }

    private static Observation CaptureForeground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return new Observation(0, "", null, null);

        GetWindowThreadProcessId(hwnd, out uint processId);
        string title = GetWindowTitle(hwnd);
        string processName = "";
        string? path = null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            path = TryGetMainModulePath(process);
        }
        catch
        {
        }

        return new Observation((int)processId, processName, path, title);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return "";

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string? TryGetMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
