using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The hover flyout that makes a scrolling capture reviewable from the corner card. The card
/// itself is pinned, so everything here is about a separate window: when it is offered at
/// all, and where it lands.
/// </summary>
public class TallCapturePreviewLayoutTests
{
    private static readonly SD.Rectangle WorkingArea = new(0, 0, 2560, 1400);
    private static readonly SD.Rectangle Card = new(2380, 1240, 160, 120);

    [Theory]
    [InlineData(320, 4000)]   // a scrolling capture
    [InlineData(4000, 320)]   // a horizontal scrolling capture
    [InlineData(400, 1200)]   // exactly 3:1 at the minimum long side
    public void LongCaptures_EarnAPreview(int width, int height)
        => Assert.True(TallCapturePreviewLayout.ShouldOffer(new SD.Size(width, height)));

    [Theory]
    [InlineData(1920, 1080)]  // an ordinary screenshot
    [InlineData(800, 400)]    // 2:1 -- the card thumbnail is still a fair review
    [InlineData(100, 390)]    // 3.9:1 but tiny; the preview would add nothing
    [InlineData(0, 0)]        // a disposed or empty image
    public void OrdinaryCaptures_DoNotGetOne(int width, int height)
        => Assert.False(TallCapturePreviewLayout.ShouldOffer(new SD.Size(width, height)));

    /// <summary>
    /// A tall strip fills the band above the card and never covers it -- the card has to stay
    /// clickable while the preview is up, since the preview exists only while it is hovered.
    /// </summary>
    [Fact]
    public void ATallStrip_FillsTheBandAboveTheCardWithoutCoveringIt()
    {
        SD.Rectangle placed = TallCapturePreviewLayout.Place(
            new SD.Size(900, 9000), WorkingArea, Card, dpi: 96);

        Assert.True(placed.Bottom <= Card.Top, "the preview must not overlap the card");
        Assert.True(WorkingArea.Contains(placed), $"{placed} escaped the working area");
        Assert.True(placed.Height > Card.Height * 4, "a monitor-height strip is the whole point");
        Assert.Equal(Card.Right, placed.Right);
    }

    /// <summary>
    /// A wide strip cannot use the vertical band, so the width cap has to bind and the height
    /// come back down with it -- otherwise the preview runs off both sides of the screen.
    /// </summary>
    [Fact]
    public void AWideStrip_IsCappedByTheWorkingAreaWidth()
    {
        SD.Rectangle placed = TallCapturePreviewLayout.Place(
            new SD.Size(9000, 700), WorkingArea, Card, dpi: 96);

        Assert.True(WorkingArea.Contains(placed), $"{placed} escaped the working area");
        Assert.True(placed.Width <= WorkingArea.Width);
        Assert.True(placed.Bottom <= Card.Top);
    }

    /// <summary>
    /// Isaiah runs two monitors above the laptop, so the card's monitor is routinely at
    /// negative Y. The placement is pure arithmetic on the passed rects, but the clamps have
    /// to survive it -- a Math.Clamp with the bounds the wrong way round throws.
    /// </summary>
    [Fact]
    public void ACardOnAMonitorAboveThePrimary_StaysOnThatMonitor()
    {
        var above = new SD.Rectangle(-1920, -1080, 1920, 1040);
        var card = new SD.Rectangle(-260, -160, 160, 120);

        SD.Rectangle placed = TallCapturePreviewLayout.Place(
            new SD.Size(400, 6000), above, card, dpi: 144);

        Assert.True(above.Contains(placed), $"{placed} escaped {above}");
        Assert.True(placed.Bottom <= card.Top);
    }

    /// <summary>A working area with no room above the card still yields a drawable rect
    /// rather than a zero or negative one.</summary>
    [Fact]
    public void ACardFlushWithTheTopOfTheScreen_StillYieldsADrawableRect()
    {
        SD.Rectangle placed = TallCapturePreviewLayout.Place(
            new SD.Size(400, 6000), WorkingArea, new SD.Rectangle(2380, 0, 160, 120), dpi: 96);

        Assert.True(placed.Width >= 1 && placed.Height >= 1);
    }
}
