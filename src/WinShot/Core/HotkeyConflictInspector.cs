using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WinShot.Core;

public sealed record HotkeyConflictSource(
    string DisplayName,
    string? LaunchTarget,
    bool IsExactMatch,
    string Detail);

public static class HotkeyConflictInspector
{
    private static readonly KnownShortcut[] KnownShortcuts =
    [
        new("Win+Shift+S", "Windows Snipping Tool", "snippingtool.exe"),
        new("Win+G", "Xbox Game Bar", "ms-gamebar:"),
        new("Win+Alt+R", "Xbox Game Bar", "ms-gamebar:"),
        new("PrintScreen", "Windows Snipping Tool", "snippingtool.exe"),
    ];

    private static readonly LikelyHotkeyApp[] LikelyApps =
    [
        new("PowerToys", ["PowerToys", "PowerToys.Settings"], "PowerToys.exe"),
        new("ShareX", ["ShareX"], "ShareX.exe"),
        new("Greenshot", ["Greenshot"], "Greenshot.exe"),
        new("Lightshot", ["Lightshot"], "Lightshot.exe"),
        new("Screenpresso", ["Screenpresso"], "Screenpresso.exe"),
        new("Snagit", ["Snagit32", "SnagitCapture", "SnagitEditor"], "Snagit32.exe"),
        new("PicPick", ["picpick"], "picpick.exe"),
        new("OBS Studio", ["obs64", "obs32"], "obs64.exe"),
        new("AutoHotkey", ["AutoHotkey", "AutoHotkey64"], "AutoHotkey.exe"),
        new("NVIDIA App / GeForce Experience", ["NVIDIA Share", "NVIDIA Overlay", "NVIDIA App"], "nvidiaapp.exe"),
        new("Flow Launcher", ["Flow.Launcher"], "Flow.Launcher.exe"),
    ];

    public readonly record struct ProcessCandidate(string ProcessName, string? MainModulePath);

    public static HotkeyConflictSource DescribeConflict(string gesture)
    {
        var candidates = new List<ProcessCandidate>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    candidates.Add(new ProcessCandidate(process.ProcessName, TryGetMainModulePath(process)));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return DescribeConflict(gesture, candidates);
    }

    public static HotkeyConflictSource DescribeConflict(string gesture, IEnumerable<ProcessCandidate> processes)
    {
        if (!HotkeyManager.TryNormalizeGesture(gesture, out string? normalized))
        {
            return new HotkeyConflictSource(
                "another app on this PC",
                "ms-settings:keyboard",
                false,
                "Windows reported this shortcut is unavailable.");
        }

        KnownShortcut? known = KnownShortcuts.FirstOrDefault(
            shortcut => string.Equals(shortcut.Gesture, normalized, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            return new HotkeyConflictSource(
                known.DisplayName,
                known.LaunchTarget,
                true,
                $"{normalized} is a known Windows shortcut.");
        }

        foreach (var candidate in processes)
        {
            if (FindLikelyApp(candidate) is { } app)
            {
                return new HotkeyConflictSource(
                    app.DisplayName,
                    string.IsNullOrWhiteSpace(candidate.MainModulePath) ? app.FallbackLaunchTarget : candidate.MainModulePath,
                    false,
                    "Windows does not expose the exact app that owns a global hotkey. This is the best running-app match.");
            }
        }

        return new HotkeyConflictSource(
            "another app on this PC",
            "ms-settings:keyboard",
            false,
            "Windows reported this shortcut is already in use, but does not expose the owner.");
    }

    public static HotkeyConflictSource DescribeProcessCandidate(ProcessCandidate candidate)
    {
        if (FindLikelyApp(candidate) is { } app)
        {
            return new HotkeyConflictSource(
                app.DisplayName,
                string.IsNullOrWhiteSpace(candidate.MainModulePath) ? app.FallbackLaunchTarget : candidate.MainModulePath,
                false,
                "This app became active immediately after the hotkey was pressed.");
        }

        string displayName = Path.GetFileNameWithoutExtension(candidate.ProcessName);
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "another app on this PC";

        return new HotkeyConflictSource(
            displayName,
            string.IsNullOrWhiteSpace(candidate.MainModulePath) ? null : candidate.MainModulePath,
            false,
            "This app became active immediately after the hotkey was pressed.");
    }

    private static LikelyHotkeyApp? FindLikelyApp(ProcessCandidate candidate)
    {
        foreach (var app in LikelyApps)
        {
            if (app.ProcessNames.Any(name => MatchesProcessName(candidate.ProcessName, name)))
                return app;
        }

        return null;
    }

    private static bool MatchesProcessName(string actual, string expected)
    {
        string normalizedActual = Path.GetFileNameWithoutExtension(actual);
        string normalizedExpected = Path.GetFileNameWithoutExtension(expected);
        return string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase);
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

    private sealed record KnownShortcut(string Gesture, string DisplayName, string LaunchTarget);

    private sealed record LikelyHotkeyApp(string DisplayName, string[] ProcessNames, string FallbackLaunchTarget);
}
