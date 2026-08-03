using WinShot.Overlay;
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
}
