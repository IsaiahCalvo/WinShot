using System.IO;
using System.Runtime.CompilerServices;
using WinShot.Core;

namespace WinShot.Tests;

/// <summary>Runs before any test: points WinShot.Core.Log at a temp dir so test runs never
/// write to the real %LOCALAPPDATA%\WinShot\logs\winshot.log.</summary>
internal static class TestLogRedirect
{
    [ModuleInitializer]
    internal static void Init() =>
        Log.DirOverride = Path.Combine(Path.GetTempPath(), "WinShotTests", "logs");
}
