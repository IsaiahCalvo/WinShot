using System.IO;
using Xunit;

namespace WinShot.Tests;

/// <summary>Locating the repository from the test binary's folder.</summary>
internal static class TestPaths
{
    /// <summary>
    /// Walks up from the test assembly to the repo root. The marker is the test runner
    /// script, not a document: THIRD_PARTY_NOTICES.md is a Content item now, so it is
    /// copied into every output folder and would stop the walk one directory too soon.
    /// </summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "run-tests.ps1")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
