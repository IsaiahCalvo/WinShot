using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Exercises the position-aware <see cref="ScrollCanvas"/> with simulated scroll sequences: forward,
/// backward (no duplication), and the hard case — a fast flick that opens a gap, then a slow
/// re-scroll back over it that must fill the gap exactly (the merge). Frames are byte-exact windows
/// of a synthetic document whose every row carries a unique color, so a misplacement is visible.
/// </summary>
public class ScrollCanvasTests
{
    private const int W = 200, H = 120;

    private static SD.Color RowColor(int r) =>
        SD.Color.FromArgb(255, r & 0xFF, (r >> 8) & 0xFF, (r * 31) & 0xFF);

    /// <summary>A viewport showing document rows [top, top+H).</summary>
    private static SD.Bitmap Window(int top)
    {
        var bmp = new SD.Bitmap(W, H, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[W * 4];
            for (int y = 0; y < H; y++)
            {
                var c = RowColor(top + y);
                for (int x = 0; x < W; x++)
                {
                    int i = x * 4;
                    row[i] = c.B; row[i + 1] = c.G; row[i + 2] = c.R; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    private static void Feed(ScrollCanvas c, params int[] tops)
    {
        foreach (int t in tops)
        {
            using var f = Window(t);
            c.Place(f);
        }
    }

    private static void AssertRowIsDoc(SD.Bitmap img, int row, int doc) =>
        Assert.Equal(RowColor(doc).ToArgb(), img.GetPixel(10, row).ToArgb());

    [Fact]
    public void ForwardScroll_ReconstructsContiguousDocument()
    {
        using var c = new ScrollCanvas();
        Feed(c, 0, 30, 60, 90); // overlap 90 each
        Assert.False(c.HasGap);

        using var img = c.Flatten()!;
        Assert.Equal(90 + H, img.Height); // last top + frame height
        AssertRowIsDoc(img, 0, 0);
        AssertRowIsDoc(img, 100, 100);
        AssertRowIsDoc(img, 209, 209);
    }

    [Fact]
    public void BackwardScroll_OverCapturedRegion_DoesNotGrowOrDuplicate()
    {
        using var c = new ScrollCanvas();
        Feed(c, 0, 30, 60);   // doc 0..179
        Feed(c, 30, 0);       // scroll back up over already-captured rows

        Assert.False(c.HasGap);
        using var img = c.Flatten()!;
        Assert.Equal(60 + H, img.Height); // unchanged — nothing new revealed
        AssertRowIsDoc(img, 0, 0);
        AssertRowIsDoc(img, 150, 150);
        AssertRowIsDoc(img, 179, 179);
    }

    [Fact]
    public void FlickGap_ThenReScrollBack_FillsGapExactly()
    {
        using var c = new ScrollCanvas();
        Feed(c, 0, 30, 60);   // doc 0..179 captured
        Feed(c, 400);         // hard flick: doc 400..519, no overlap -> floating segment + gap
        Assert.True(c.HasGap);

        // Slowly scroll back UP from the flick toward the top; each step overlaps the previous,
        // walking the floating segment up until a frame overlaps BOTH it and the main canvas.
        Feed(c, 370, 340, 310, 280, 250, 220, 190, 160, 130, 100);

        Assert.False(c.HasGap); // bridged + merged: the gap is closed
        using var img = c.Flatten()!;
        Assert.Equal(520, img.Height); // full document 0..519, no compression
        AssertRowIsDoc(img, 0, 0);
        AssertRowIsDoc(img, 250, 250);   // a row that lived inside the former gap
        AssertRowIsDoc(img, 399, 399);   // last row of the former gap
        AssertRowIsDoc(img, 519, 519);   // the flick segment's tail
    }

    [Fact]
    public void FlickGap_TransientBadFrame_DoesNotNukeRecovery()
    {
        using var c = new ScrollCanvas();
        Feed(c, 0, 30, 60);   // main doc 0..179
        Feed(c, 400);         // flick -> floating
        Feed(c, 370, 340);    // grow the floating segment
        Feed(c, 9000);        // ONE garbage frame that matches neither — must be dropped, not nuke recovery
        Feed(c, 310, 280, 250, 220, 190, 160, 130, 100); // keep walking back

        Assert.False(c.HasGap); // still bridges despite the bad frame
        using var img = c.Flatten()!;
        Assert.Equal(520, img.Height);
        AssertRowIsDoc(img, 250, 250); // a former-gap row survived
    }

    [Fact]
    public void UnbridgedFlick_FlattensWithMarkerBand()
    {
        using var c = new ScrollCanvas();
        Feed(c, 0, 30);   // doc 0..149
        Feed(c, 400);     // flick, never re-scrolled
        Assert.True(c.HasGap);

        using var img = c.Flatten()!;
        // main (150) + marker band (22) + floating segment (120)
        Assert.Equal(150 + 22 + 120, img.Height);
        AssertRowIsDoc(img, 149, 149);                                  // end of the top segment
        Assert.Equal(SD.Color.FromArgb(255, 90, 74, 42).ToArgb(),
                     img.GetPixel(10, 160).ToArgb());                   // inside the marker band
        AssertRowIsDoc(img, 172, 400);                                  // flick segment starts after marker
    }

    [Fact]
    public void Pause_IdenticalFrame_DoesNotGrow()
    {
        using var c = new ScrollCanvas();
        Feed(c, 0, 30);
        int before = c.Height;
        Feed(c, 30); // same position again
        Assert.Equal(before, c.Height);
        Assert.False(c.HasGap);
    }
}
