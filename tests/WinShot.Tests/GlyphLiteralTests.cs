using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Segoe Fluent glyphs are private-use characters. Typed into source as the characters
/// themselves they look like nothing in most editors and diff tools, and a merge in September
/// silently turned every capture verb's glyph in the tray menu into "": the top group of the
/// menu lost its icons while the group below kept them, and nothing failed. Written as
/// <c></c> escapes they survive any tool that touches the file.
/// </summary>
public class GlyphLiteralTests
{
    private static readonly Regex PrivateUse = new("[-]", RegexOptions.Compiled);

    [Fact]
    public void GlyphsOutsideTheEditorAreWrittenAsEscapes()
    {
        string src = Path.Combine(TestPaths.RepoRoot(), "src", "WinShot");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}Editor{Path.DirectorySeparatorChar}"))
            .Where(path => PrivateUse.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(src, path))
            .ToList();
        Assert.True(offenders.Count == 0, "literal glyph characters in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryTrayCaptureVerbHasAGlyph()
    {
        string app = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "src", "WinShot", "App.xaml.cs"));
        var emptied = Regex.Matches(app, @"(?:MenuItem|Row)\(""(?<name>[^""]+)""[^\n]*, """"\)\)?;")
            .Select(m => m.Groups["name"].Value)
            .ToList();
        Assert.True(emptied.Count == 0, "tray rows with an empty glyph: " + string.Join(", ", emptied));
    }
}
