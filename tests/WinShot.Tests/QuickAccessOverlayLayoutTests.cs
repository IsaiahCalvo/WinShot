using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class QuickAccessOverlayLayoutTests
{
    [Fact]
    public void SmallFootprint_MatchesVerifiedLogicalScale()
    {
        var layout = QuickAccessOverlayLayout.Calculate(new SD.Size(1512, 982), sizePercent: 10, dpi: 96);

        Assert.Equal(new SD.Size(158, 110), layout.ClientSize);
        Assert.Equal(28, layout.IconSize);
        Assert.Equal(36, layout.PillWidth);
        Assert.Equal(32, layout.PillHeight);
    }

    [Theory]
    [InlineData(96, 190, 120, 28, 32)]
    [InlineData(144, 285, 180, 42, 48)]
    [InlineData(192, 380, 241, 56, 64)]
    public void Layout_ScalesForHighDpi(int dpi, int expectedWidth, int expectedHeight, int icon, int pillHeight)
    {
        var layout = QuickAccessOverlayLayout.Calculate(new SD.Size(1200, 760), sizePercent: 50, dpi);

        Assert.Equal(expectedWidth, layout.ClientSize.Width);
        Assert.Equal(expectedHeight, layout.ClientSize.Height);
        Assert.Equal(icon, layout.IconSize);
        Assert.Equal(pillHeight, layout.PillHeight);
        Assert.True(layout.CardBounds.Contains(layout.ThumbnailBounds));
    }

    [Fact]
    public void BottomRight_ClampsInsideNegativeCoordinateMonitor()
    {
        var area = new SD.Rectangle(-1920, -120, 1920, 1040);
        var size = new SD.Size(158, 109);

        SD.Point point = QuickAccessOverlayLayout.Place(area, size, "bottom-right", stackOffset: 0, dpi: 96);

        Assert.Equal(new SD.Point(-174, 795), point);
        Assert.True(area.Contains(new SD.Rectangle(point, size)));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("top")]
    [InlineData("bottom")]
    [InlineData("bottom-left")]
    [InlineData("bottom-right")]
    public void EveryPosition_StaysInsideWorkingArea(string position)
    {
        var area = new SD.Rectangle(1440, -900, 2560, 1400);
        var size = new SD.Size(285, 181);

        SD.Point point = QuickAccessOverlayLayout.Place(area, size, position, stackOffset: 600, dpi: 144);

        Assert.True(area.Contains(new SD.Rectangle(point, size)));
    }

    [Fact]
    public void UnknownPosition_FallsBackToBottomRight()
    {
        var area = new SD.Rectangle(0, 0, 1920, 1040);
        var size = new SD.Size(190, 120);

        Assert.Equal(
            QuickAccessOverlayLayout.Place(area, size, "bottom-right", 0, 96),
            QuickAccessOverlayLayout.Place(area, size, "stale-value", 0, 96));
    }

    [Theory]
    [InlineData("copy-close", "copy-close")]
    [InlineData("close", "close")]
    [InlineData("save-close", "save-close")]
    [InlineData("unknown", "save-close")]
    public void AutoCloseAction_IsNormalized(string input, string expected)
        => Assert.Equal(expected, QuickAccessOverlayLayout.NormalizeAutoCloseAction(input));

    [Theory]
    [InlineData("ask", "ask")]
    [InlineData("ASK", "ask")]
    [InlineData("export", "export")]
    [InlineData("unknown", "export")]
    public void SaveBehavior_IsNormalized(string input, string expected)
        => Assert.Equal(expected, QuickAccessOverlayLayout.NormalizeSaveBehavior(input));

    [Fact]
    public void NewSettings_DefaultToBottomRight()
        => Assert.Equal("bottom-right", new Settings().OverlayPosition);
}
