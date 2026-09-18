using System.Globalization;
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

    /// <summary>
    /// The general case of the test above, and the one that would have caught the nine that
    /// were still there after it passed: NO control anywhere may draw a font code point as its
    /// Content. Even on a style that does set FontFamily — where the glyph renders rather than
    /// tofus — a Segoe MDL2 code point beside a ported path icon is a second icon family on
    /// the same row, with its own stroke weight, corner radius and optical size. That is the
    /// loudest tell a UI was not drawn by one hand, and the bottom bar shipped three of them.
    /// </summary>
    ///
    /// Content= was the only half this used to look at, which is why it passed over History and
    /// Settings for two whole rounds: those two windows typed their twenty-one glyphs into a
    /// TextBlock's Text=, and a TextBlock drawing an icon is a control drawing an icon. Both
    /// spellings now, and the app has none of either.
    [Fact]
    public void NoControlAnywhereRendersAFontCodePointAsItsContent()
    {
        var offenders = new List<string>();
        foreach (string f in SourceFiles(RepoRoot()).Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            // The icon dictionary itself names, in prose, the code points it replaced.
            if (Path.GetFileName(f).Equals("SurveyToolIcons.xaml", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (string tag in File.ReadAllText(f).Split('<'))
            {
                if (tag.StartsWith("!--", StringComparison.Ordinal)) continue;
                if (!Regex.IsMatch(tag, @"(Content|Text)=""&#x[0-9A-Fa-f]{4};""")) continue;
                string name = Regex.Match(tag, @"x:Name=""([A-Za-z0-9_]+)""") is { Success: true } n
                    ? n.Groups[1].Value : tag.Split('\n')[0].Trim();
                offenders.Add($"{Path.GetFileName(f)}:{name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A font code point as Content is a second icon family on the row. Draw it as a "
            + "SurveyIcon.* path on the 24-unit grid instead: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// One family means ONE line weight. Every glyph in SurveyToolIcons.xaml is a Viewbox of a
    /// fixed pixel size wrapping a Canvas of grid units, so what actually gets painted is
    /// StrokeThickness x (viewbox px / canvas units) x (any scale the glyph carries inside).
    /// This computes that for every stroked path and asserts they all land on one number.
    ///
    /// It used to be nine numbers — 1.0 to 2.625 px — and because the dictionary did not hold
    /// a weight, each consumer compensated by resizing the HOST: the toolbar wrapped Undo at
    /// 14 px, Crop at 20 and Select at 22 to fake a matching line, and the sizes then did not
    /// match either. Undo's arrow measured two thirds the size of Crop's frame beside it.
    /// </summary>
    [Fact]
    public void EveryGlyphInTheIconFamilyPaintsTheSameStrokeWeight()
    {
        string dict = Path.Combine(RepoRoot(), "src", "WinShot", "Theme", "SurveyToolIcons.xaml");
        string xaml = File.ReadAllText(dict);

        double houseUnits = double.Parse(
            Regex.Match(xaml, @"Property=""StrokeThickness"" Value=""([\d.]+)""").Groups[1].Value,
            CultureInfo.InvariantCulture);

        var painted = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string block in Regex.Split(xaml, @"(?=<Viewbox x:Key=)").Skip(1))
        {
            string key = Regex.Match(block, @"x:Key=""([^""]+)""").Groups[1].Value;
            var vb = Regex.Match(block, @"<Viewbox[^>]*?\sWidth=""([\d.]+)""");
            var canvas = Regex.Match(block, @"<Canvas[^>]*\sWidth=""([\d.]+)""");
            Assert.True(vb.Success, $"{key}: no Viewbox size, so it stretches to whatever host "
                + "it lands in and its stroke stretches with it.");
            if (!canvas.Success) continue;                       // fill-only glyph on no grid

            double host = double.Parse(vb.Groups[1].Value, CultureInfo.InvariantCulture);
            double grid = double.Parse(canvas.Groups[1].Value, CultureInfo.InvariantCulture);
            double inner = Regex.Match(block, @"<ScaleTransform ScaleX=""([\d.]+)""") is { Success: true } sc
                ? double.Parse(sc.Groups[1].Value, CultureInfo.InvariantCulture) : 1.0;

            foreach (string tag in block.Split('<'))
            {
                if (!tag.Contains("SurveyIconStroke", StringComparison.Ordinal)) continue;
                var own = Regex.Match(tag, @"StrokeThickness=""([\d.]+)""");
                double units = own.Success
                    ? double.Parse(own.Groups[1].Value, CultureInfo.InvariantCulture)
                    : houseUnits;
                // An inner scale only applies where the glyph declares one; a path outside it
                // would be measured wrong, but no glyph in this dictionary mixes the two.
                double scale = own.Success && inner != 1.0 ? inner : 1.0;
                painted[$"{key} ({units})"] = Math.Round(units * scale * host / grid, 2);
            }
        }

        Assert.NotEmpty(painted);
        var weights = painted.Values.Distinct().OrderBy(v => v).ToList();
        double house = painted.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        Assert.True(weights.Count == 1,
            $"The icon family paints {weights.Count} different line weights ("
            + string.Join(", ", weights) + $" px) where {house} px is the house. Set each "
            + "glyph's StrokeThickness so it paints the house weight on its own grid: "
            + string.Join("; ", painted.Where(kv => kv.Value != house)
                                       .Select(kv => $"{kv.Key} -> {kv.Value} px")));
    }

    /// <summary>
    /// The C# half of the same rule, for the two windows this round converted. A private-use
    /// code point handed to a WPF control from code-behind is the same defect the XAML rule
    /// catches, spelled somewhere that rule cannot see: History kept five of them — three empty
    /// states and two thumbnail placeholders — as \uE9xx string constants.
    /// </summary>
    [Fact]
    public void NeitherHistoryNorSettingsHandsAControlAPrivateUseCodePoint()
    {
        var offenders = new List<string>();
        foreach (string dir in new[] { "History", "SettingsUi" })
        foreach (string f in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src", "WinShot", dir), "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text, @"\\u(E[0-9A-Fa-f]{3})"))
                offenders.Add($"{Path.GetFileName(f)}: U+{m.Groups[1].Value}");
            // And the literal character, which is what a paste from the font viewer leaves.
            if (text.Any(c => c is >= '\uE000' and <= '\uF8FF'))
                offenders.Add($"{Path.GetFileName(f)}: a private-use character, pasted literally");
        }

        Assert.True(offenders.Count == 0,
            "These name a typeface glyph where they should name a SurveyIcon.* resource key: "
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
