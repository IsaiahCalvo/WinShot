using WinShot.Core;
using WinShot.Overlay;
using Xunit;

namespace WinShot.Tests;

public class QuickAccessTooltipLayoutTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void LayoutScalesWithoutClippingText(int dpi)
    {
        using var font = ThemePalette.UiFont(6.5f);
        QuickAccessTooltipLayout layout = QuickAccessTooltipLayout.Calculate("Open Annotate tool (Ctrl+E)", font, dpi);

        Assert.True(layout.Size.Width > layout.TextBounds.Width);
        Assert.True(layout.Size.Height > layout.TextBounds.Height);
        Assert.True(new System.Drawing.Rectangle(System.Drawing.Point.Empty, layout.Size).Contains(layout.TextBounds));
        Assert.True(layout.CornerRadius > 0);
    }

    [Fact]
    public void DarkAndLightTooltipEvidenceRendersOffScreen()
    {
        using var dark = QuickAccessTooltipRenderer.RenderEvidence(
            "Copy (Ctrl+C)",
            QuickAccessOverlayThemePalette.For(QuickAccessOverlayTheme.Dark));
        using var light = QuickAccessTooltipRenderer.RenderEvidence(
            "Copy (Ctrl+C)",
            QuickAccessOverlayThemePalette.For(QuickAccessOverlayTheme.Light));

        Assert.Equal(dark.Size, light.Size);
        Assert.NotEqual(
            dark.GetPixel(dark.Width / 2, dark.Height / 2),
            light.GetPixel(light.Width / 2, light.Height / 2));
    }

    [Fact]
    public void PlacementStaysInsideNegativeCoordinateMonitorWithoutUsingTheDesktop()
    {
        var workingArea = new System.Drawing.Rectangle(-1920, -1080, 1920, 1040);
        var card = new System.Drawing.Rectangle(-220, -190, 190, 120);
        var anchor = new System.Drawing.Rectangle(-211, -181, 28, 28);

        System.Drawing.Point point = QuickAccessTooltipLayout.Place(
            workingArea,
            new System.Drawing.Size(52, 28),
            anchor,
            card,
            96);

        Assert.True(workingArea.Contains(new System.Drawing.Rectangle(point, new System.Drawing.Size(52, 28))));
        Assert.True(point.X > anchor.Right);
        Assert.InRange(point.Y, anchor.Top - 8, anchor.Bottom);
    }

    [Fact]
    public void CenterControlTooltipFallsBelowWhenThereIsNoRoomAbove()
    {
        var workingArea = new System.Drawing.Rectangle(0, 0, 1280, 720);
        var card = new System.Drawing.Rectangle(20, 4, 190, 120);
        var anchor = new System.Drawing.Rectangle(76, 8, 78, 27);

        System.Drawing.Point point = QuickAccessTooltipLayout.Place(
            workingArea,
            new System.Drawing.Size(52, 28),
            anchor,
            card,
            96);

        Assert.True(point.Y > anchor.Bottom);
        Assert.True(point.Y < card.Bottom);
    }

    [Fact]
    public void RightCornerTooltipSitsImmediatelyToTheLeftOfItsButton()
    {
        var workingArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var card = new System.Drawing.Rectangle(1700, 900, 192, 120);
        var anchor = new System.Drawing.Rectangle(1855, 909, 28, 28);
        var size = new System.Drawing.Size(31, 19);

        System.Drawing.Point point = QuickAccessTooltipLayout.Place(workingArea, size, anchor, card, 96);

        Assert.Equal(anchor.Left - size.Width - 5, point.X);
        Assert.InRange(point.Y, anchor.Top, anchor.Bottom);
    }
}
