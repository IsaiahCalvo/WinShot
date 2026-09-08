using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The rubric's 4.5:1 floor for text, measured against the tokens themselves rather than
/// eyeballed. The audit found three failures here: white on the accent (3.65:1) under every
/// primary button, white on the destructive red (3.4:1), and helper text at 40% white
/// (3.67:1). Reading Theme.xaml directly keeps this a pure test with no WPF Application.
/// </summary>
public class ThemeContrastTests
{
    private static readonly Dictionary<string, (byte A, byte R, byte G, byte B)> Argb = LoadColors();

    /// <summary>The opaque part of a token, which is what most of these assertions want.</summary>
    private static Dictionary<string, (byte R, byte G, byte B)> Colors =>
        _rgb ??= Argb.ToDictionary(e => e.Key, e => (e.Value.R, e.Value.G, e.Value.B), StringComparer.Ordinal);

    private static Dictionary<string, (byte R, byte G, byte B)>? _rgb;

    private static Dictionary<string, (byte, byte, byte, byte)> LoadColors()
    {
        string path = Path.Combine(RepoRoot(), "src", "WinShot", "Theme", "Theme.xaml");
        string xaml = File.ReadAllText(path);
        var map = new Dictionary<string, (byte, byte, byte, byte)>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(xaml, @"<Color x:Key=""(?<key>\w+)"">#(?<hex>[0-9A-Fa-f]{6,8})</Color>"))
        {
            string hex = m.Groups["hex"].Value;
            byte alpha = 255;
            if (hex.Length == 8)
            {
                alpha = Convert.ToByte(hex[..2], 16);
                hex = hex[2..];
            }
            map[m.Groups["key"].Value] = (
                alpha,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        return map;
    }

    private static string RepoRoot() => TestPaths.RepoRoot();

    private static double Channel(byte value)
    {
        double c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance((byte R, byte G, byte B) c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast((byte, byte, byte) a, (byte, byte, byte) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Alpha-white over an opaque backdrop, which is how the text tokens are declared.</summary>
    private static (byte, byte, byte) WhiteOver(double alpha, (byte R, byte G, byte B) bg) => (
        (byte)Math.Round(bg.R + alpha * (255 - bg.R)),
        (byte)Math.Round(bg.G + alpha * (255 - bg.G)),
        (byte)Math.Round(bg.B + alpha * (255 - bg.B)));

    private static readonly (byte, byte, byte) White = ((byte)255, (byte)255, (byte)255);

    [Theory]
    [InlineData("AccentDeepColor")]
    [InlineData("RedDeepColor")]
    public void WhiteLabelsOnFilledButtons_Clear45To1(string fillToken)
    {
        Assert.True(Colors.ContainsKey(fillToken), $"{fillToken} is missing from Theme.xaml");
        double ratio = Contrast(White, Colors[fillToken]);
        Assert.True(ratio >= 4.5, $"white on {fillToken} is {ratio:0.00}:1");
    }

    /// <summary>One token composited over another, the way a template's Hover/Press layer
    /// sits over its rest fill at Opacity 1.</summary>
    private static (byte, byte, byte) Over(string layerToken, (byte R, byte G, byte B) under)
    {
        (byte a, byte r, byte g, byte b) = Argb[layerToken];
        double alpha = a / 255.0;
        return (
            (byte)Math.Round(under.R + alpha * (r - under.R)),
            (byte)Math.Round(under.G + alpha * (g - under.G)),
            (byte)Math.Round(under.B + alpha * (b - under.B)));
    }

    /// <summary>
    /// The whole state ladder of a filled pill, not just its resting fill. Deepening
    /// AccentDeep/RedDeep for contrast is undone the instant the pointer arrives if hover
    /// lightens: the destructive pill shipped a hover of #FF6B60 at 2.79:1 — a worse reading
    /// than the 3.4:1 the deepening existed to fix, and at exactly the moment the user is
    /// looking at the button. Both templates now dip on hover and dip further on press, and
    /// this asserts the composited result rather than the token in isolation.
    /// </summary>
    [Theory]
    [InlineData("AccentDeepColor")]
    [InlineData("RedDeepColor")]
    public void WhiteLabelsClear45To1_Hovered_AndPressed(string fillToken)
    {
        var rest = Colors[fillToken];
        var hovered = Over("HoverShadeColor", rest);
        var pressed = Over("PressedShadeColor", hovered);

        Assert.True(Contrast(White, hovered) >= 4.5,
            $"white on hovered {fillToken} is {Contrast(White, hovered):0.00}:1");
        Assert.True(Contrast(White, pressed) >= 4.5,
            $"white on pressed {fillToken} is {Contrast(White, pressed):0.00}:1");

        // Direction, not just the floor: a saturated fill must never LIGHTEN under white type.
        Assert.True(Contrast(White, hovered) > Contrast(White, rest),
            $"{fillToken} lightens on hover; a saturated fill dips");
        Assert.True(Contrast(White, pressed) > Contrast(White, hovered),
            $"{fillToken} lightens on press");
    }

    /// <summary>PillButtonPrimary's press layer is an authored opaque blue rather than the
    /// shared dip, so it needs its own reading.</summary>
    [Fact]
    public void TheAccentsAuthoredPressedFillAlsoClearsTheFloor()
    {
        double ratio = Contrast(White, Colors["AccentPressedColor"]);
        Assert.True(ratio >= 4.5, $"white on AccentPressedColor is {ratio:0.00}:1");
    }

    [Fact]
    public void TheDeepenedFillsAreStillTheSameAccentAndRed()
    {
        // "One accent" is a hard constraint: the deepened fill exists for contrast, not as a
        // second identity, so it must stay recognisably the same hue.
        Assert.True(Hue(Colors["AccentDeepColor"]) - Hue(Colors["AccentColor"]) is > -12 and < 12);
        Assert.True(Hue(Colors["RedDeepColor"]) - Hue(Colors["AnnotationRedColor"]) is > -12 and < 12);
    }

    private static double Hue((byte R, byte G, byte B) c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        if (d == 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    [Theory]
    [InlineData("ToolbarBgColor")]  // the settings card surface
    [InlineData("WindowBgColor")]   // the window behind it
    public void HelperTextClears45To1OnEverySurfaceItLandsOn(string surface)
    {
        // TextHintColor is #8CFFFFFF: 0x8C/255 white over the backdrop.
        var hint = WhiteOver(0x8C / 255.0, Colors[surface]);
        double ratio = Contrast(hint, Colors[surface]);
        Assert.True(ratio >= 4.5, $"hint text on {surface} is {ratio:0.00}:1");
    }

    [Fact]
    public void PrimaryAndSecondaryTextStillClearTheFloor()
    {
        var card = Colors["ToolbarBgColor"];
        Assert.True(Contrast(WhiteOver(1.0, card), card) >= 4.5);       // TextPrimary is opaque-ish
        Assert.True(Contrast(WhiteOver(0xB3 / 255.0, card), card) >= 4.5); // TextSecondary
    }

    /// <summary>
    /// Token-level assertions all passed while two templates still painted white on the raw
    /// accent: DarkComboBoxItem and DarkMenuItem set the fill and the text colour inside a
    /// trigger, and nothing here read triggers. Any trigger that names BOTH a Background and a
    /// Foreground is declaring a text-on-fill pair, so measure it like one.
    /// </summary>
    [Fact]
    public void EveryTriggerThatPaintsTextOnAFillClears45To1()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "WinShot", "Theme", "Theme.xaml"));

        // Brush key -> the Color key it wraps, so a Background="{StaticResource FooBrush}" resolves.
        var brushes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(xaml,
                     @"<SolidColorBrush\s+x:Key=""(?<brush>\w+)""\s+Color=""\{StaticResource (?<color>\w+)\}"""))
            brushes[m.Groups["brush"].Value] = m.Groups["color"].Value;

        int checkedPairs = 0;
        foreach (Match trigger in Regex.Matches(xaml, @"<Trigger\b.*?</Trigger>", RegexOptions.Singleline))
        {
            string body = trigger.Value;
            Match bg = Regex.Match(body, @"Property=""(?:\w+\.)?Background"" Value=""\{StaticResource (?<t>\w+)\}""");
            if (!bg.Success) continue;

            if (Argb.TryGetValue(ColorKey(bg.Groups["t"].Value), out var fillArgb) is false || fillArgb.A != 255)
                continue;
            var fill = (fillArgb.R, fillArgb.G, fillArgb.B);

            foreach (Match fg in Regex.Matches(body, @"Property=""(?:\w+\.)?Foreground"" Value=""(?<v>[^""]+)"""))
            {
                string value = fg.Groups["v"].Value;
                (byte, byte, byte) text;
                if (string.Equals(value, "White", StringComparison.Ordinal))
                {
                    text = White;
                }
                else
                {
                    Match token = Regex.Match(value, @"^\{StaticResource (?<k>\w+)\}$");
                    if (!token.Success) continue;
                    if (!Argb.TryGetValue(ColorKey(token.Groups["k"].Value), out var textArgb)) continue;
                    // Translucent text composites over the fill it lands on — which is exactly
                    // what the menu's shortcut column does at #B3FFFFFF over the accent.
                    text = textArgb.A == 255
                        ? (textArgb.R, textArgb.G, textArgb.B)
                        : WhiteOver(textArgb.A / 255.0, fill);
                }

                checkedPairs++;
                double ratio = Contrast(text, fill);
                Assert.True(ratio >= 4.5,
                    $"{value} on {bg.Groups["t"].Value} is {ratio:0.00}:1 — " +
                    "a filled row carrying a light label wants AccentDeep, not Accent");
            }
        }

        // A regex that matched nothing would pass silently, which is how this slipped once.
        Assert.True(checkedPairs >= 3, $"only {checkedPairs} text-on-fill trigger pairs found — the scan stopped working");

        string ColorKey(string key) => brushes.TryGetValue(key, out string? c) ? c : key;
    }

    [Fact]
    public void HintIsQuieterThanSecondaryText_SoTheHierarchySurvivedTheFix()
    {
        var card = Colors["ToolbarBgColor"];
        double hint = Contrast(WhiteOver(0x8C / 255.0, card), card);
        double secondary = Contrast(WhiteOver(0xB3 / 255.0, card), card);
        Assert.True(hint < secondary, $"hint {hint:0.00} should read quieter than secondary {secondary:0.00}");
    }
}
