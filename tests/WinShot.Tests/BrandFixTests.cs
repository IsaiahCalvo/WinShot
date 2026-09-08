using System.Windows;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The rules the fix round added, each one a thing that shipped wrong once: Mica may only
/// take a window's background away where Mica exists, the WinForms palette must carry the
/// same white-on-accent fills Theme.xaml gained, and a standing screenshot backdrop must not
/// frame a shot the interactive composer is about to frame.
/// </summary>
public class BrandFixTests
{
    // ------------------------------------------------------------------ Mica

    [Theory]
    // Win11 22H2 and later: the backdrop exists, so trading the opaque background for it works.
    [InlineData(false, 22621, true)]
    [InlineData(false, 26200, true)]
    // Win10 20H1 (a supported target) and Win11 21H2: DwmSetWindowAttribute(38) is rejected
    // and nothing paints the ground the transparent window gave up.
    [InlineData(false, 19041, false)]
    [InlineData(false, 22000, false)]
    // High Contrast owns its own background on every build; ApplyHighContrastPalette sets one
    // seven lines before ApplyMica runs and Mica used to overwrite it with transparency.
    [InlineData(true, 26200, false)]
    [InlineData(true, 19041, false)]
    public void MicaOnlyTakesTheBackgroundWhereMicaExists(bool highContrast, int build, bool expected)
        => Assert.Equal(expected, DarkTitleBar.CanUseMica(highContrast, build));

    // -------------------------------------------------------------- contrast

    private static double Relative(SD.Color c)
    {
        static double Channel(int v)
        {
            double s = v / 255d;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double ContrastWithWhite(SD.Color c) => 1.05 / (Relative(c) + 0.05);

    [Fact]
    public void TheWinFormsPaletteCarriesFillsThatCanHoldWhiteText()
    {
        // Theme.xaml grew AccentDeep/RedDeep precisely because white on the raw accent is
        // 3.65:1 and on the raw red 3.4:1, and ThemeContrastTests pins the WPF half. The
        // WinForms mirror got no such tokens, and two new surfaces — the tray menu's hovered
        // row and the first-run "Got it" button — put white on the raw accent.
        Assert.True(ContrastWithWhite(ThemePalette.AccentDeep) >= 4.5, $"{ContrastWithWhite(ThemePalette.AccentDeep):0.00}:1");
        Assert.True(ContrastWithWhite(ThemePalette.RedDeep) >= 4.5, $"{ContrastWithWhite(ThemePalette.RedDeep):0.00}:1");
        Assert.True(ContrastWithWhite(ThemePalette.AccentPressed) >= 4.5);
        // And the reason they exist: the plain accent cannot hold white text.
        Assert.True(ContrastWithWhite(ThemePalette.Accent) < 4.5);
    }

    [Fact]
    public void TheDeepFillsAreTheSameColoursTheThemeDeclares()
    {
        // One palette, two halves. These are Theme.xaml's AccentDeepColor and RedDeepColor.
        Assert.Equal(SD.Color.FromArgb(0x0A, 0x73, 0xE0).ToArgb(), ThemePalette.AccentDeep.ToArgb());
        Assert.Equal(SD.Color.FromArgb(0xD4, 0x2B, 0x21).ToArgb(), ThemePalette.RedDeep.ToArgb());
        // Same hue family as the accent, so the app still reads as having one.
        Assert.InRange(ThemePalette.AccentDeep.GetHue() - ThemePalette.Accent.GetHue(), -12f, 12f);
    }

    // -------------------------------------------------------- tray menu glyphs

    [Theory]
    [InlineData(96, 16)]
    [InlineData(120, 20)]
    [InlineData(144, 24)]   // Isaiah's primary
    [InlineData(192, 32)]
    public void TrayMenuGlyphsAreRasterisedForTheirMonitor(int dpi, int expected)
    {
        // The landmine: WinForms scales a ToolStrip's font and row height and does nothing
        // at all for an Image, so a fixed 16 px raster shrinks against its own row as the
        // DPI rises. Both the bitmap and the point size come off DeviceDpi now, and the
        // cache is keyed by it so moving the menu between monitors re-rasterises.
        Assert.Equal(expected, WinShot.App.MenuGlyphPixels(dpi));
    }

    // ------------------------------------------------- one backdrop, not two

    [Theory]
    // Every ordinary capture still gets the standing backdrop, whatever else it is doing.
    [InlineData("gradient", null, true)]
    [InlineData("wallpaper", PostCaptureAction.Overlay, true)]
    [InlineData("color", PostCaptureAction.Edit, true)]
    [InlineData("gradient", PostCaptureAction.Pin, true)]
    // "Capture window with background" is on its way to the interactive composer, which
    // frames the shot itself: framing it here first is a frame inside a frame.
    [InlineData("gradient", PostCaptureAction.Background, false)]
    [InlineData("wallpaper", PostCaptureAction.Background, false)]
    [InlineData("color", PostCaptureAction.Background, false)]
    // And "none" is still none.
    [InlineData("none", null, false)]
    [InlineData(null, PostCaptureAction.Background, false)]
    public void TheStandingBackdropIsSkippedForTheBackgroundComposer(
        string? mode, string? overrideAction, bool expected)
        => Assert.Equal(expected, ScreenshotBackdrop.AppliesTo(mode, overrideAction));

    [Fact]
    public void ASecondComposeIsWhatTheGuardIsPreventing()
    {
        // Why it matters: composing twice is not a no-op, it is a visibly nested frame that
        // reaches history and the saved file with no error anywhere.
        using var shot = new SD.Bitmap(400, 300);
        using SD.Bitmap? once = ScreenshotBackdrop.Compose(shot, "gradient", null);
        Assert.NotNull(once);
        using SD.Bitmap? twice = ScreenshotBackdrop.Compose(once!, "gradient", null);
        Assert.NotNull(twice);
        Assert.True(twice!.Width > once!.Width, "a second compose re-frames — that is the bug");
    }
}
