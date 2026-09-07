using WinShot.Overlay;
using SD = System.Drawing;
using Xunit;

namespace WinShot.Tests;

public class QuickAccessOverlayThemeTests
{
    [Fact]
    public void CurrentTheme_MatchesDarkOnlyApplication()
        => Assert.Equal(QuickAccessOverlayTheme.Dark, QuickAccessOverlayThemePalette.Current);

    [Fact]
    public void LightAndDarkPalettes_KeepControlsReadable()
    {
        var light = QuickAccessOverlayThemePalette.For(QuickAccessOverlayTheme.Light);
        var dark = QuickAccessOverlayThemePalette.For(QuickAccessOverlayTheme.Dark);

        Assert.True(light.Glyph.GetBrightness() < 0.2f);
        Assert.True(dark.Glyph.GetBrightness() > 0.8f);
        Assert.True(light.ControlFace.A < byte.MaxValue);
        Assert.True(dark.ControlFace.A < byte.MaxValue);
        Assert.True(light.HoverTint.A > 0);
        Assert.True(dark.HoverTint.A > 0);
        Assert.NotEqual(light.CardFill, dark.CardFill);
    }

    [Fact]
    public void TheRecordingStripStaysReadableOverTheWorstCapturePossible()
    {
        foreach (QuickAccessOverlayTheme theme in new[] { QuickAccessOverlayTheme.Light, QuickAccessOverlayTheme.Dark })
            AssertStripIsReadable(theme);
    }

    private static void AssertStripIsReadable(QuickAccessOverlayTheme theme)
    {
        var visuals = QuickAccessOverlayThemePalette.For(theme);

        // A recording of a bright document and a recording of a black terminal are both
        // ordinary; the scrim has to carry the duration and the size over either.
        foreach (SD.Color capture in new[] { SD.Color.White, SD.Color.Black })
        {
            SD.Color behind = Over(visuals.MediaStripScrim, capture);
            Assert.True(
                Contrast(visuals.Glyph, behind) >= 4.5,
                $"{theme} strip text reads at {Contrast(visuals.Glyph, behind):0.00}:1 over {capture.Name}");
        }
    }

    private static SD.Color Over(SD.Color top, SD.Color under)
    {
        double a = top.A / 255d;
        return SD.Color.FromArgb(
            (int)Math.Round(top.R * a + under.R * (1 - a)),
            (int)Math.Round(top.G * a + under.G * (1 - a)),
            (int)Math.Round(top.B * a + under.B * (1 - a)));
    }

    private static double Contrast(SD.Color a, SD.Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(SD.Color c)
        => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte value)
    {
        double v = value / 255d;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
