using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// A WPF TextBox charges its Padding TWICE when the template also hangs it on the content
/// host: once as the host's Margin, and once inside the control, which insets its own text
/// view by Padding as well.
///
/// DarkTextBox asked for 8,5 inside a 30 px field. One pixel of border each side leaves 28;
/// five top and bottom, twice, spends 20 of it; and 8 px is not enough for a 17 px line. Every
/// field that used the style — the Resize dialog's width, height and scale, the editor colour
/// picker's hex field, the background composer's — rendered a 3 px sliver of its own value and
/// nothing else. The numbers you were about to resize to were unreadable.
///
/// It survived every review because Settings looked fine, and Settings looked fine because its
/// own TextBox style happens to set 8,0. Pure text analysis: no WPF, safe in a full run.
/// </summary>
public class TextFieldPaddingTests
{
    [Fact]
    public void NoTextBoxStyleSpendsVerticalPaddingTwice()
    {
        string theme = Path.Combine(RepoRoot(), "src", "WinShot", "Theme", "Theme.xaml");
        string xaml = File.ReadAllText(theme);

        var offenders = new List<string>();
        foreach (Match style in Regex.Matches(
            xaml, @"<Style x:Key=""(?<key>[^""]+)""[^>]*TargetType=""TextBox""(?<body>.*?)</Style>",
            RegexOptions.Singleline))
        {
            string body = style.Groups["body"].Value;
            // Only styles whose template pins the content host to Padding pay it twice.
            if (!Regex.IsMatch(body, @"PART_ContentHost[^>]*Margin=""\{TemplateBinding Padding\}"""))
                continue;

            var pad = Regex.Match(body, @"Property=""Padding"" Value=""(?<v>[^""]+)""");
            if (!pad.Success) continue;

            var parts = pad.Groups["v"].Value.Split(',')
                .Select(p => double.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
            double top = parts.Length switch { 1 => parts[0], 2 => parts[1], 4 => parts[1], _ => 0 };
            double bottom = parts.Length switch { 1 => parts[0], 2 => parts[1], 4 => parts[3], _ => 0 };
            if (top > 0 || bottom > 0)
                offenders.Add($"{style.Groups["key"].Value} (Padding {pad.Groups["v"].Value})");
        }

        Assert.True(offenders.Count == 0,
            "These TextBox styles hang Padding on PART_ContentHost AND ask for vertical padding, "
            + "so the control charges it twice and clips its own text. Use 0 top and bottom and "
            + "let VerticalContentAlignment centre the line: " + string.Join(", ", offenders));
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
