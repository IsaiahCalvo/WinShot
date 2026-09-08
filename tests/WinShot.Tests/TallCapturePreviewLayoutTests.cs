using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

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

    /// <summary>
    /// A card with no room above it gets no preview at all. There used to be a 120 px floor on
    /// the band, which grew the flyout back down over the card it is meant to float above --
    /// and the card is the thing the preview exists to make reviewable.
    /// </summary>
    [Theory]
    [InlineData(0)]      // flush with the top of the screen
    [InlineData(100)]    // a band of 76 px after the gaps
    public void ACardWithNoBandAboveIt_GetsNoPreview(int cardTop)
    {
        SD.Rectangle placed = TallCapturePreviewLayout.Place(
            new SD.Size(400, 6000), WorkingArea, new SD.Rectangle(2380, cardTop, 160, 120), dpi: 96);

        Assert.True(placed.IsEmpty, $"{placed} was offered with only {cardTop} px above the card");
    }

    /// <summary>
    /// The preview scales the card's LIVE bitmap, and the card renders its own thumbnail from
    /// the same object on a background thread under lock(image). GDI+ locks an Image for the
    /// duration of an operation, so the preview has to take that same lock or the two
    /// DrawImage calls collide with "Object is currently in use elsewhere" -- and the loser is
    /// usually the thumbnail, which then stays empty for the life of the card. Proven by
    /// holding the lock and checking that TryCreate waits for it.
    /// </summary>
    [Fact]
    public void TheHoverPreview_TakesTheSameLockTheThumbnailRenderTakes()
    {
        using var source = new SD.Bitmap(320, 4000, SD.Imaging.PixelFormat.Format32bppPArgb);
        var visuals = QuickAccessOverlayThemePalette.For(QuickAccessOverlayThemePalette.Current);
        var held = new ManualResetEventSlim();

        var holder = new Thread(() =>
        {
            lock (source)
            {
                held.Set();
                Thread.Sleep(300);
            }
        }) { IsBackground = true };
        holder.Start();
        held.Wait(5000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using TallCapturePreviewWindow? preview =
            TallCapturePreviewWindow.TryCreate(source, Card, dpi: 96, visuals);
        sw.Stop();
        holder.Join(5000);

        Assert.NotNull(preview);
        Assert.True(sw.ElapsedMilliseconds >= 150,
            $"TryCreate returned in {sw.ElapsedMilliseconds} ms while the source was locked, so it never took the lock.");
    }

    /// <summary>
    /// The flyout fades in on hover and out again, and it is created and destroyed on every
    /// pass of the pointer over the card -- so a stranded tween here would raise the process
    /// timer resolution once per hover and never put it back.
    /// </summary>
    [Fact]
    public void HoveringAndLeaving_LeavesNoTweenHoldingTheTimer()
    {
        Assert.Equal(0, Motion.Holders);
        using var source = new SD.Bitmap(320, 4000, SD.Imaging.PixelFormat.Format32bppPArgb);
        var visuals = QuickAccessOverlayThemePalette.For(QuickAccessOverlayThemePalette.Current);

        TallCapturePreviewWindow? preview =
            TallCapturePreviewWindow.TryCreate(source, Card, dpi: 96, visuals);
        Assert.NotNull(preview);

        preview!.StartPosition = WF.FormStartPosition.Manual;
        preview.Location = new SD.Point(-20_000, -20_000);
        preview.Show();
        WF.Application.DoEvents();

        // Out before in has finished, which is what a pointer crossing the card actually does.
        preview.FadeOutAndClose();
        PumpUntil(() => Motion.Holders == 0);

        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>Closing mid-fade must not strand the hold either.</summary>
    [Fact]
    public void ClosingMidFade_ReleasesTheMotionHold()
    {
        Assert.Equal(0, Motion.Holders);
        using var source = new SD.Bitmap(320, 4000, SD.Imaging.PixelFormat.Format32bppPArgb);
        var visuals = QuickAccessOverlayThemePalette.For(QuickAccessOverlayThemePalette.Current);

        TallCapturePreviewWindow? preview =
            TallCapturePreviewWindow.TryCreate(source, Card, dpi: 96, visuals);
        Assert.NotNull(preview);

        preview!.StartPosition = WF.FormStartPosition.Manual;
        preview.Location = new SD.Point(-20_000, -20_000);
        preview.Show();
        WF.Application.DoEvents();
        preview.Dispose();
        PumpUntil(() => Motion.Holders == 0);

        Assert.Equal(0, Motion.Holders);
    }

    private static void PumpUntil(Func<bool> condition, int ms = 5000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < ms)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }
}
