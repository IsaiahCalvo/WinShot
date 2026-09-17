using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Every floating layer in the app has to be the same card.
///
/// Round 5 never rendered one. A Popup's content is a separate top-level window, so a
/// RenderTargetBitmap of the owner reaches none of it, and five different cards had grown up
/// behind that blind spot: three corner radii (the fit menu 2, the eraser menu 6, everything
/// else 8), four drop shadows — two written inline on the Border — and two border brushes, on
/// menus that open a hand's width apart in the same toolbar. The fit menu reserved no margin
/// at all, so the shadow it did ask for was clipped away by its own popup window and it was
/// the one card in the app that floated with nothing under it.
///
/// Alongside them, three styles for one menu row (the fit menu's had no hover feedback of any
/// kind) and Windows' own Separator, which is a near-white rule and was the brightest thing on
/// the History card menu.
///
/// Pure text analysis: no WPF, no Application, safe in a full run.
/// </summary>
public class PopoverFamilyTests
{
    [Fact]
    public void EveryPopupCardIsThePopoverCard()
    {
        var offenders = new List<string>();
        foreach (string f in XamlFiles())
        {
            string text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text, @"<Popup\b.*?>\s*(<!--.*?-->\s*)*<(?<tag>[A-Za-z]+)(?<attrs>[^>]*)",
                         RegexOptions.Singleline))
            {
                // The size popover wraps its card in a StackPanel to hang the pointer arrow
                // above it; the card is the Border inside, which the next match picks up.
                string tag = m.Groups["tag"].Value;
                if (tag is "StackPanel" or "ScrollViewer") continue;
                if (tag != "Border")
                {
                    offenders.Add($"{Path.GetFileName(f)}: popup child is <{tag}>, not a card");
                    continue;
                }
                if (!m.Groups["attrs"].Value.Contains("StaticResource PopoverCard", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(f)}: popup card does not use PopoverCard");
            }
        }

        Assert.True(offenders.Count == 0,
            "A popover that carries its own radius, border or shadow is a second card in an app "
            + "that should have one. Use Style=\"{StaticResource PopoverCard}\": "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// A Popup window is sized to its child, so a shadow only has room to fall if the child
    /// reserves it. The fit menu asked for a 24px blur with a 10px drop and a Margin of 0, and
    /// rendered with no shadow whatsoever.
    /// </summary>
    [Fact]
    public void EveryPopoverCardReservesRoomForItsShadow()
    {
        var offenders = new List<string>();
        foreach (string f in XamlFiles())
        {
            string text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text, @"StaticResource PopoverCard\}""(?<attrs>[^>]*)"))
            {
                var margin = Regex.Match(m.Groups["attrs"].Value, @"Margin=""([^""]+)""");
                if (!margin.Success) continue;          // inherits the style's own 0,6,36,36
                var n = margin.Groups[1].Value.Split(',');
                if (n.Length != 4 || double.Parse(n[2]) < 36 || double.Parse(n[3]) < 36)
                    offenders.Add($"{Path.GetFileName(f)}: Margin=\"{margin.Groups[1].Value}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "A popover card that overrides Margin must still leave 36px right and bottom for "
            + "ChromePopoverShadow, or the popup window clips the shadow away: "
            + string.Join("; ", offenders));
    }

    /// <summary>One menu row, not three. ChromeMenuItem is it.</summary>
    [Fact]
    public void ThereIsOneMenuRowStyle()
    {
        string theme = File.ReadAllText(ThemePath());
        foreach (string dead in new[] { "MenuRowButton", "EraserModeRow" })
            Assert.False(theme.Contains($"x:Key=\"{dead}\"", StringComparison.Ordinal),
                $"{dead} is a second menu row style; its rows and ChromeMenuItem's differ in "
                + "height, type size, radius and hover feedback. Fold it into ChromeMenuItem.");
        Assert.Contains("x:Key=\"ChromeMenuItem\"", theme, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows' Separator is a near-white line with its own margins. Dropped into a #323236
    /// menu it is brighter than the labels it divides — three of them ran across the History
    /// card menu. A Separator inside a menu takes MenuItem.SeparatorStyleKey and ignores an
    /// implicit style, so the theme has to supply both.
    /// </summary>
    [Fact]
    public void NoSeparatorDrawsWindowsOwnRule()
    {
        string theme = File.ReadAllText(ThemePath());
        Assert.Contains("x:Key=\"DarkSeparator\"", theme, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"Separator\" BasedOn=\"{StaticResource DarkSeparator}\"/>",
            theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"{x:Static MenuItem.SeparatorStyleKey}\"", theme, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every glyph in the family strokes itself from {RelativeSource AncestorType=Control}
    /// Foreground. A bare ContentPresenter puts no Control in the content's parent chain, so
    /// the binding resolves to nothing and the glyph draws with a null stroke — invisible. The
    /// eyedropper row in the colour picker was drawing nothing at all for exactly this reason.
    /// </summary>
    [Fact]
    public void NoFamilyGlyphIsHostedInABareContentPresenter()
    {
        var offenders = new List<string>();
        foreach (string f in XamlFiles())
            foreach (Match m in Regex.Matches(File.ReadAllText(f),
                         @"<ContentPresenter[^>]*Content=""\{StaticResource (SurveyIcon\.[A-Za-z0-9_]+)\}"))
                offenders.Add($"{Path.GetFileName(f)}:{m.Groups[1].Value}");

        Assert.True(offenders.Count == 0,
            "Host a SurveyIcon in a ContentControl, not a ContentPresenter — the stroke binding "
            + "needs a Control above it or the glyph renders invisible: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// StripToolButton's template wraps its content in a fixed 20x20 Viewbox, which is right
    /// for one glyph and scales a whole sentence down to three pixels tall. "Pick from image"
    /// was illegible in the colour picker for that reason. A labelled row uses StripToolRow.
    /// </summary>
    [Fact]
    public void NoStripToolButtonCarriesALabel()
    {
        var offenders = new List<string>();
        foreach (string f in XamlFiles())
        {
            string text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text,
                         @"<(?<type>RadioButton|ToggleButton|Button)\b(?<attrs>[^>]*StaticResource StripToolButton[^>]*)>(?<body>.*?)</\k<type>>",
                         RegexOptions.Singleline))
            {
                if (!Regex.IsMatch(m.Groups["body"].Value, @"<TextBlock\b[^>]*Text=""[^""]{2,}")) continue;
                string name = Regex.Match(m.Groups["attrs"].Value, @"x:Name=""([A-Za-z0-9_]+)""") is { Success: true } n
                    ? n.Groups[1].Value : "(unnamed)";
                offenders.Add($"{Path.GetFileName(f)}:{name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "StripToolButton is an icon chip — its 20x20 Viewbox shrinks any label put inside "
            + "it to nothing. Use StripToolRow for a row that carries words: "
            + string.Join(", ", offenders));
    }

    private static string ThemePath() =>
        Path.Combine(RepoRoot(), "src", "WinShot", "Theme", "Theme.xaml");

    private static IEnumerable<string> XamlFiles()
    {
        string src = Path.Combine(RepoRoot(), "src");
        foreach (string f in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return f;
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
