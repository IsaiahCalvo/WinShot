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
    private static readonly Dictionary<string, (byte R, byte G, byte B)> Colors = LoadColors();

    private static Dictionary<string, (byte, byte, byte)> LoadColors()
    {
        string path = Path.Combine(RepoRoot(), "src", "WinShot", "Theme", "Theme.xaml");
        string xaml = File.ReadAllText(path);
        var map = new Dictionary<string, (byte, byte, byte)>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(xaml, @"<Color x:Key=""(?<key>\w+)"">#(?<hex>[0-9A-Fa-f]{6,8})</Color>"))
        {
            string hex = m.Groups["hex"].Value;
            if (hex.Length == 8) hex = hex[2..]; // drop alpha; composited separately below
            map[m.Groups["key"].Value] = (
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

    [Fact]
    public void HintIsQuieterThanSecondaryText_SoTheHierarchySurvivedTheFix()
    {
        var card = Colors["ToolbarBgColor"];
        double hint = Contrast(WhiteOver(0x8C / 255.0, card), card);
        double secondary = Contrast(WhiteOver(0xB3 / 255.0, card), card);
        Assert.True(hint < secondary, $"hint {hint:0.00} should read quieter than secondary {secondary:0.00}");
    }
}
