using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Every glyph in SurveyToolIcons.xaml must be rendered by something.
///
/// This exists because two of them were not. SurveyIcon.Blur and SurveyIcon.Pixelate were
/// left behind when the re-editable redaction mark replaced Survey's separate Blur and
/// Pixelate tools, and nothing noticed for a whole polish round — they kept being re-ported,
/// re-measured and certified against Survey while no button on screen drew them. Meanwhile
/// the Redact and Magnifier buttons that SHOULD have had glyphs were rendering Segoe MDL2
/// code points as text and coming out as tofu boxes. Dead resources and missing icons are
/// the same bug seen from two ends, and a similarity score cannot see either one.
///
/// Pure text analysis: no WPF, no Application, safe in a full run.
/// </summary>
public class IconResourceAuditTests
{
    [Fact]
    public void EverySurveyIconResourceIsReferencedSomewhere()
    {
        string repo = RepoRoot();
        string dictPath = Path.Combine(repo, "src", "WinShot", "Theme", "SurveyToolIcons.xaml");
        Assert.True(File.Exists(dictPath), $"icon dictionary not found at {dictPath}");

        var declared = Regex.Matches(File.ReadAllText(dictPath), @"x:Key=""(SurveyIcon\.[A-Za-z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(declared);

        // Everything that could name one, minus the dictionary itself and anything built.
        var sources = SourceFiles(repo).Where(f =>
            !string.Equals(f, dictPath, StringComparison.OrdinalIgnoreCase)).ToList();

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string f in sources)
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"SurveyIcon\.[A-Za-z0-9_]+"))
                used.Add(m.Value);

        var orphans = declared.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(orphans.Count == 0,
            "Unreferenced icon resources in SurveyToolIcons.xaml — wire them to a control or "
            + "delete them; an icon nothing renders is an icon nobody can check: "
            + string.Join(", ", orphans));
    }

    /// <summary>
    /// The mirror of the above: nothing in the editor toolbar may draw an icon as a FONT
    /// glyph on a style that never sets FontFamily. That is exactly how Redact and Magnifier
    /// became tofu boxes — StripToolButton sets no FontFamily, so a Segoe MDL2 code point
    /// set as Content fell through to the UI font, which has nothing at U+E7E6.
    /// </summary>
    [Fact]
    public void NoStripToolButtonRendersAFontCodePointAsItsContent()
    {
        string editor = Path.Combine(RepoRoot(), "src", "WinShot", "Editor", "EditorWindow.xaml");
        string xaml = File.ReadAllText(editor);

        // Each control element, split on '<' so a match is scoped to one tag.
        var offenders = xaml.Split('<')
            .Where(tag => tag.Contains("StripToolButton", StringComparison.Ordinal))
            .Where(tag => Regex.IsMatch(tag, @"Content=""&#x[0-9A-Fa-f]{4};"""))
            .Select(tag => Regex.Match(tag, @"x:Name=""([A-Za-z0-9_]+)""") is { Success: true } n
                ? n.Groups[1].Value : tag.Split('\n')[0].Trim())
            .ToList();

        Assert.True(offenders.Count == 0,
            "StripToolButton sets no FontFamily, so a font code point as Content renders as a "
            + "tofu box. Give these a SurveyIcon.* path resource instead: "
            + string.Join(", ", offenders));
    }

    private static IEnumerable<string> SourceFiles(string repo)
    {
        foreach (string dir in new[] { Path.Combine(repo, "src"), Path.Combine(repo, "tests") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                // Skip build output: obj/ carries generated .g.cs and .baml that would make a
                // resource look "used" purely because it was compiled in.
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    continue;
                string ext = Path.GetExtension(f);
                if (ext is ".xaml" or ".cs") yield return f;
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "WinShot")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
