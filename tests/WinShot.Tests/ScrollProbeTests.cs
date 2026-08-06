using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Headless fixtures for the wheel-probe region refinement: a virtual screen where only a
/// sub-rectangle scrolls, plus a flickering "video" block that changes without input. The
/// probe must shrink the region to the scrolling pane, ignore the animation, restore the
/// scroll position, and keep the user's rect untouched when nothing moves.
/// </summary>
public class ScrollProbeTests
{
    private const int W = 800;
    private const int H = 600;
    private static readonly SD.Rectangle Region = new(0, 0, W, H);
    private static readonly SD.Rectangle Pane = new(240, 40, 420, 500);
    private static readonly SD.Rectangle Video = new(20, 60, 160, 120);

    [Fact]
    public void ShrinksToTheScrollingPane_IgnoringAnimation()
    {
        int offset = 0, tick = 0;
        SD.Bitmap Grab(SD.Rectangle r) => Render(r, offset, animationPhase: tick++, withVideo: true);
        void Wheel(int delta) => offset += delta < 0 ? 150 : -150;

        SD.Rectangle refined = ScrollProbe.Refine(Region, hint: null, Grab, Wheel, sleep: _ => { });

        // Without a hint the probe crops to ink (+pad); it must stay inside the pane
        // (slightly inflated), never bleed into chrome, and keep a usable width.
        var paneWithPad = Pane;
        paneWithPad.Inflate(20, 20);
        Assert.True(paneWithPad.Contains(refined), $"refined {refined} escapes pane {Pane}");
        Assert.True(refined.Width >= 250 && refined.Height >= 300, $"refined {refined} too small");
        Assert.True(!refined.IntersectsWith(Video), $"refined {refined} includes the video block");
        Assert.Equal(0, offset); // the probe put the page back
    }

    [Fact]
    public void HintSnapsToThePanesRealBorder()
    {
        int offset = 0;
        SD.Bitmap Grab(SD.Rectangle r) => Render(r, offset, animationPhase: 0, withVideo: false);
        void Wheel(int delta) => offset += delta < 0 ? 150 : -150;
        var hint = new PaneInfo(Pane, PaneSource.UiaVerified);

        SD.Rectangle refined = ScrollProbe.Refine(Region, hint, Grab, Wheel, sleep: _ => { });

        Assert.Equal(Pane.Left, refined.Left);
        Assert.Equal(Pane.Right, refined.Right);
    }

    [Fact]
    public void MovedIslandAcrossAGap_DoesNotDragThePaneEdge()
    {
        // A static side rail whose pixels shift slightly WITH the wheel (scrollbar thumb,
        // hover flicker) is a moved island across a wide static gutter — it must not pull
        // the pane's right edge across the gap (live: Outputs/Sources rail sliced into a
        // chat capture).
        var rail = new SD.Rectangle(W - 60, 200, 40, 240);
        int offset = 0;
        SD.Bitmap Grab(SD.Rectangle r)
        {
            SD.Bitmap bmp = Render(r, offset, animationPhase: 0, withVideo: false);
            for (int y = rail.Top; y < rail.Bottom; y++)
            for (int x = rail.Left; x < rail.Right; x++)
                bmp.SetPixel(x - r.X, y - r.Y, SD.Color.FromArgb(255, 90 + (offset / 10 + x + y) % 40, 80, 80));
            return bmp;
        }
        void Wheel(int delta) => offset += delta < 0 ? 150 : -150;

        SD.Rectangle refined = ScrollProbe.Refine(Region, hint: null, Grab, Wheel, sleep: _ => { });

        Assert.True(refined.Right <= Pane.Right + 24, $"refined {refined} crossed the gutter toward the rail");
    }

    [Fact]
    public void NothingMoves_KeepsTheUserRect()
    {
        SD.Bitmap Grab(SD.Rectangle r) => Render(r, 0, animationPhase: 0, withVideo: false);
        SD.Rectangle refined = ScrollProbe.Refine(Region, hint: null, Grab, _ => { }, sleep: _ => { });
        Assert.Equal(Region, refined);
    }

    /// <summary>Static chrome everywhere; text-like scrolling content inside Pane (offset by
    /// the virtual scroll); a flickering block inside Video when enabled.</summary>
    private static SD.Bitmap Render(SD.Rectangle region, int offset, int animationPhase, bool withVideo)
    {
        var bmp = new SD.Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, region.Width, region.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[region.Width * 4];
            for (int y = 0; y < region.Height; y++)
            {
                for (int x = 0; x < region.Width; x++)
                {
                    int sx = region.X + x, sy = region.Y + y;
                    SD.Color c;
                    if (Pane.Contains(sx, sy))
                    {
                        // ponytail: reuse the aperiodic text-line pattern proven in the
                        // seam-heal fixtures, keyed to the scrolled document row.
                        c = Doc(sx - Pane.X, sy - Pane.Y + offset);
                    }
                    else if (withVideo && Video.Contains(sx, sy))
                    {
                        int v = 40 + (animationPhase * 53 + sx * 7 + sy * 3) % 160;
                        c = SD.Color.FromArgb(255, v, 40, 160 - v / 2);
                    }
                    else
                    {
                        c = SD.Color.FromArgb(255, 32, 34, 40);
                    }
                    int i = x * 4;
                    row[i] = c.B; row[i + 1] = c.G; row[i + 2] = c.R; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private static SD.Color Doc(int x, int documentY)
    {
        int segment = documentY / 97;
        int lineHeight = 13 + (Mix(5, segment) & 7);
        int line = segment * 8 + (documentY % 97) / lineHeight;
        int lineHash = Mix(11, line);
        int rowInLine = (documentY % 97) % lineHeight;
        bool inTextBand = rowInLine >= 3 && rowInLine <= lineHeight - 5;
        int wordPeriod = 18 + (lineHash & 31);
        int wordLen = 7 + ((lineHash >> 5) & 7);
        bool ink = inTextBand && ((x + (lineHash >> 8)) % wordPeriod) < wordLen &&
            x < 60 + ((lineHash >> 12) & 255);
        return ink
            ? SD.Color.FromArgb(255, 40 + (lineHash & 31), 44, 52)
            : SD.Color.FromArgb(255, 238, 240, 243);
    }

    private static int Mix(int x, int y)
    {
        unchecked
        {
            uint value = (uint)(x * 374761393 + y * 668265263 + 12345);
            value = (value ^ (value >> 13)) * 1274126177u;
            return (int)(value ^ (value >> 16));
        }
    }
}
