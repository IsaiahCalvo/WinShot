using System.Diagnostics;
using System.Reflection;

namespace WinShot.Core;

/// <summary>Read-only facts about the running build, shown in the About tab and tray menu.</summary>
public static class AppInfo
{
    /// <summary>Marketing version, e.g. "1.2.0". Falls back to the assembly version when the
    /// informational version carries build metadata (a "+sha" suffix from CI).</summary>
    public static string Version { get; } = ResolveVersion();

    public static string DisplayName => "WinShot";

    /// <summary>"WinShot 1.2.0" — the one-line label for menus and headers.</summary>
    public static string VersionLabel => $"{DisplayName} {Version}";

    public static string RepositoryUrl => "https://github.com/IsaiahCalvo/WinShot";

    private static string ResolveVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        // InformationalVersion mirrors <Version> from the csproj (and is what CI stamps).
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+'); // strip "+<gitsha>" build metadata if present
            return plus > 0 ? info[..plus] : info;
        }
        Version? v = asm.GetName().Version;
        return v is null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
