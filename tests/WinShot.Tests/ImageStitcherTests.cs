using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class ImageStitcherTests
{
    private const int Width = 320;
    private const int Height = 400;

    /// <summary>
    /// Deterministic, collision-free per-row color: R/G encode the global row
    /// index (unique up to 65536 rows), B adds extra variation.
    /// </summary>
    private static SD.Color RowColor(int globalRow) => SD.Color.FromArgb(
        255, globalRow & 0xFF, (globalRow >> 8) & 0xFF, (globalRow * 31) & 0xFF);

    /// <summary>
    /// Builds a frame whose row y shows "document" row <paramref name="topGlobalRow"/> + y,
    /// simulating a viewport scrolled to that position. Written via LockBits so every
    /// pixel is exact (no anti-aliasing).
    /// </summary>
    private static SD.Bitmap MakeFrame(int topGlobalRow, int height = Height)
    {
        var bmp = new SD.Bitmap(Width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, Width, height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Width * 4];
            for (int y = 0; y < height; y++)
            {
                var c = RowColor(topGlobalRow + y);
                for (int x = 0; x < Width; x++)
                {
                    int i = x * 4;
                    row[i] = c.B;
                    row[i + 1] = c.G;
                    row[i + 2] = c.R;
                    row[i + 3] = 255;
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

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(150)]
    public void FindScrollOffset_DetectsScrollAmount(int k)
    {
        using var frame1 = MakeFrame(0);
        using var frame2 = MakeFrame(k); // frame1 scrolled up by k rows, new rows revealed below

        Assert.Equal(k, ImageStitcher.FindScrollOffset(frame1, frame2));
    }

    [Fact]
    public void FindScrollOffset_TooFewOverlappingRows_ReturnsZero()
    {
        using var frame1 = MakeFrame(0);
        using var frame2 = MakeFrame(Height - 10); // only 10 rows overlap, below the 24-row floor

        Assert.Equal(0, ImageStitcher.FindScrollOffset(frame1, frame2));
    }

    [Fact]
    public void FindScrollOffset_UnrelatedFrames_ReturnsZero()
    {
        using var frame1 = MakeFrame(0);
        using var frame2 = MakeFrame(10_000); // no shared content at any offset

        Assert.Equal(0, ImageStitcher.FindScrollOffset(frame1, frame2));
    }

    [Fact]
    public void FindScrollOffset_RepeatedStaticRows_ReturnsZero()
    {
        using var frame1 = MakeSolidFrame(SD.Color.White);
        using var frame2 = MakeSolidFrame(SD.Color.White);

        Assert.Equal(0, ImageStitcher.FindScrollOffset(frame1, frame2));
    }

    [Fact]
    public void FindScrollOffset_MismatchedSizes_ReturnsZero()
    {
        using var frame1 = MakeFrame(0);
        using var frame2 = MakeFrame(0, Height + 8);

        Assert.Equal(0, ImageStitcher.FindScrollOffset(frame1, frame2));
    }

    [Fact]
    public void AppendBelow_GrowsHeightByNewRows()
    {
        using var stitched = MakeFrame(0);
        using var current = MakeFrame(150);
        using var result = ImageStitcher.AppendBelow(stitched, current, 150);

        Assert.Equal(Width, result.Width);
        Assert.Equal(Height + 150, result.Height);
    }

    [Fact]
    public void AppendBelow_AppendsExactBottomRowsOfCurrent()
    {
        using var stitched = MakeFrame(0);   // document rows 0..399
        using var current = MakeFrame(150);  // document rows 150..549
        using var result = ImageStitcher.AppendBelow(stitched, current, 150);

        // Last original row, first appended row, last appended row.
        Assert.Equal(RowColor(399).ToArgb(), result.GetPixel(10, 399).ToArgb());
        Assert.Equal(RowColor(400).ToArgb(), result.GetPixel(10, 400).ToArgb());
        Assert.Equal(RowColor(549).ToArgb(), result.GetPixel(10, 549).ToArgb());
    }

    [Fact]
    public void AppendBelow_ZeroNewRows_ReturnsCopyOfStitched()
    {
        using var stitched = MakeFrame(0);
        using var current = MakeFrame(150);
        using var result = ImageStitcher.AppendBelow(stitched, current, 0);

        Assert.Equal(Height, result.Height);
        Assert.Equal(RowColor(0).ToArgb(), result.GetPixel(10, 0).ToArgb());
        Assert.Equal(RowColor(399).ToArgb(), result.GetPixel(10, 399).ToArgb());
    }

    [Fact]
    public void AppendBelow_NewRowsClampedToCurrentHeight()
    {
        using var stitched = MakeFrame(0);
        using var current = MakeFrame(400);
        using var result = ImageStitcher.AppendBelow(stitched, current, Height + 500);

        Assert.Equal(Height * 2, result.Height); // clamped to all of current
        Assert.Equal(RowColor(400).ToArgb(), result.GetPixel(10, 400).ToArgb());
        Assert.Equal(RowColor(799).ToArgb(), result.GetPixel(10, 799).ToArgb());
    }

    // ---- Horizontal scrolling (column-wise mirrors of the tests above) ----

    private const int HWidth = 400;
    private const int HHeight = 320;

    /// <summary>Column counterpart of <see cref="RowColor"/> — same collision-free encoding.</summary>
    private static SD.Color ColColor(int globalCol) => RowColor(globalCol);

    /// <summary>
    /// Builds a frame whose column x shows "document" column <paramref name="leftGlobalCol"/> + x,
    /// simulating a viewport scrolled horizontally to that position. Written via LockBits so
    /// every pixel is exact (no anti-aliasing).
    /// </summary>
    private static SD.Bitmap MakeFrameHorizontal(int leftGlobalCol, int width = HWidth)
    {
        var bmp = new SD.Bitmap(width, HHeight, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, width, HHeight),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Every row is identical: pixel x carries the color of document column
            // leftGlobalCol + x. Build it once and stamp it onto each row.
            var row = new byte[width * 4];
            for (int x = 0; x < width; x++)
            {
                var c = ColColor(leftGlobalCol + x);
                int i = x * 4;
                row[i] = c.B;
                row[i + 1] = c.G;
                row[i + 2] = c.R;
                row[i + 3] = 255;
            }
            for (int y = 0; y < HHeight; y++)
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(150)]
    public void FindScrollOffsetHorizontal_DetectsScrollAmount(int k)
    {
        using var frame1 = MakeFrameHorizontal(0);
        using var frame2 = MakeFrameHorizontal(k); // frame1 scrolled left by k columns, new columns revealed on the right

        Assert.Equal(k, ImageStitcher.FindScrollOffsetHorizontal(frame1, frame2));
    }

    [Fact]
    public void FindScrollOffsetHorizontal_TooFewOverlappingCols_ReturnsZero()
    {
        using var frame1 = MakeFrameHorizontal(0);
        using var frame2 = MakeFrameHorizontal(HWidth - 10); // only 10 columns overlap, below the 24-column floor

        Assert.Equal(0, ImageStitcher.FindScrollOffsetHorizontal(frame1, frame2));
    }

    [Fact]
    public void FindScrollOffsetHorizontal_UnrelatedFrames_ReturnsZero()
    {
        using var frame1 = MakeFrameHorizontal(0);
        using var frame2 = MakeFrameHorizontal(10_000); // no shared content at any offset

        Assert.Equal(0, ImageStitcher.FindScrollOffsetHorizontal(frame1, frame2));
    }

    [Fact]
    public void FindScrollOffsetHorizontal_RepeatedStaticColumns_ReturnsZero()
    {
        using var frame1 = MakeSolidFrame(SD.Color.White, HWidth, HHeight);
        using var frame2 = MakeSolidFrame(SD.Color.White, HWidth, HHeight);

        Assert.Equal(0, ImageStitcher.FindScrollOffsetHorizontal(frame1, frame2));
    }

    [Fact]
    public void FindScrollOffsetHorizontal_MismatchedSizes_ReturnsZero()
    {
        using var frame1 = MakeFrameHorizontal(0);
        using var frame2 = MakeFrameHorizontal(0, HWidth + 8);

        Assert.Equal(0, ImageStitcher.FindScrollOffsetHorizontal(frame1, frame2));
    }

    [Fact]
    public void AppendRight_GrowsWidthByNewCols()
    {
        using var stitched = MakeFrameHorizontal(0);
        using var current = MakeFrameHorizontal(150);
        using var result = ImageStitcher.AppendRight(stitched, current, 150);

        Assert.Equal(HHeight, result.Height);
        Assert.Equal(HWidth + 150, result.Width);
    }

    [Fact]
    public void AppendRight_AppendsExactRightColsOfCurrent()
    {
        using var stitched = MakeFrameHorizontal(0);   // document cols 0..399
        using var current = MakeFrameHorizontal(150);  // document cols 150..549
        using var result = ImageStitcher.AppendRight(stitched, current, 150);

        // Last original column, first appended column, last appended column.
        Assert.Equal(ColColor(399).ToArgb(), result.GetPixel(399, 10).ToArgb());
        Assert.Equal(ColColor(400).ToArgb(), result.GetPixel(400, 10).ToArgb());
        Assert.Equal(ColColor(549).ToArgb(), result.GetPixel(549, 10).ToArgb());
    }

    [Fact]
    public void AppendRight_ZeroNewCols_ReturnsCopyOfStitched()
    {
        using var stitched = MakeFrameHorizontal(0);
        using var current = MakeFrameHorizontal(150);
        using var result = ImageStitcher.AppendRight(stitched, current, 0);

        Assert.Equal(HWidth, result.Width);
        Assert.Equal(ColColor(0).ToArgb(), result.GetPixel(0, 10).ToArgb());
        Assert.Equal(ColColor(399).ToArgb(), result.GetPixel(399, 10).ToArgb());
    }

    [Fact]
    public void AppendRight_NewColsClampedToCurrentWidth()
    {
        using var stitched = MakeFrameHorizontal(0);
        using var current = MakeFrameHorizontal(400);
        using var result = ImageStitcher.AppendRight(stitched, current, HWidth + 500);

        Assert.Equal(HWidth * 2, result.Width); // clamped to all of current
        Assert.Equal(ColColor(400).ToArgb(), result.GetPixel(400, 10).ToArgb());
        Assert.Equal(ColColor(799).ToArgb(), result.GetPixel(799, 10).ToArgb());
    }

    private static SD.Bitmap MakeSolidFrame(SD.Color color, int width = Width, int height = Height)
    {
        var bmp = new SD.Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }

    /// <summary>Distinctive, body-disjoint per-row color for a sticky band (header/footer).</summary>
    private static SD.Color BandColor(int bandRow) => RowColor(1_000_000 + bandRow);

    // ====================================================================
    // Whitespace-robust offset
    // ====================================================================

    /// <summary>
    /// Builds a frame with a tall band of identical blank rows in the MIDDLE, flanked by
    /// distinct content. Row y shows: distinct content for the top/bottom thirds, a constant
    /// blank color for the middle third. The blank band shares one hash, so an offset that
    /// aligns only blank rows must be rejected; the real offset aligns the distinct content.
    /// </summary>
    private static SD.Bitmap MakeWhitespaceBandFrame(int topGlobalRow)
    {
        var bmp = new SD.Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, Width, Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int blankStart = Height / 3, blankEnd = 2 * Height / 3;
            var row = new byte[Width * 4];
            for (int y = 0; y < Height; y++)
            {
                int doc = topGlobalRow + y;
                bool blank = y >= blankStart && y < blankEnd;
                var c = blank ? SD.Color.FromArgb(255, 250, 250, 250) : RowColor(doc);
                for (int x = 0; x < Width; x++)
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

    [Fact]
    public void FindScrollOffset_WhitespaceBandBetweenContent_DetectsRealOffset()
    {
        const int k = 24;
        using var frame1 = MakeWhitespaceBandFrame(0);
        using var frame2 = MakeWhitespaceBandFrame(k);

        // The blank middle band shares a hash across many rows, but the distinct content above
        // and below anchors the true offset — the search must not lock onto a blank-only match.
        Assert.Equal(k, ImageStitcher.FindScrollOffset(frame1, frame2));
    }

    [Fact]
    public void FindScrollOffset_AllWhitespace_ReturnsZero()
    {
        // A frame that is blank everywhere (no distinctive content) must not produce a bogus
        // offset from coincidental blank-row alignment.
        using var frame1 = MakeSolidFrame(SD.Color.FromArgb(255, 250, 250, 250));
        using var frame2 = MakeSolidFrame(SD.Color.FromArgb(255, 250, 250, 250));

        Assert.Equal(0, ImageStitcher.FindScrollOffset(frame1, frame2));
    }

    // ====================================================================
    // End-to-end stitch loop (mirrors ScrollingCaptureService's manual path)
    // ====================================================================

    /// <summary>
    /// Builds a small viewport frame of <paramref name="w"/>×<paramref name="h"/> showing document
    /// rows starting at <paramref name="topGlobalRow"/>, with an optional constant sticky header of
    /// <paramref name="header"/> rows pinned at the top (body-disjoint colors, never moves).
    /// </summary>
    private static SD.Bitmap MakeViewport(int topGlobalRow, int w, int h, int header)
    {
        var bmp = new SD.Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                // Header rows are sticky chrome (constant across frames); body rows scroll.
                var c = y < header ? BandColor(y) : RowColor(topGlobalRow + (y - header));
                for (int x = 0; x < w; x++)
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

    /// <summary>
    /// Drives the stitcher exactly as ScrollingCaptureService's manual loop does — FindScrollOffset
    /// then AppendBelow, re-anchoring on a zero offset — over a sequence of slowly-scrolled frames,
    /// and returns the final stitched height. No bands, no footer handling.
    /// </summary>
    private static int RunStitchLoop(IReadOnlyList<SD.Bitmap> frames)
    {
        SD.Bitmap? stitched = null, previous = null;
        try
        {
            foreach (var frame in frames)
            {
                if (stitched is null) { stitched = (SD.Bitmap)frame.Clone(); previous = (SD.Bitmap)frame.Clone(); continue; }

                int offset = ImageStitcher.FindScrollOffset(previous!, frame);
                if (offset == 0)
                {
                    if (!ImageStitcher.FramesIdentical(previous!, frame)) { previous!.Dispose(); previous = (SD.Bitmap)frame.Clone(); }
                    continue;
                }
                var grown = ImageStitcher.AppendBelow(stitched, frame, offset);
                stitched.Dispose(); stitched = grown;
                previous!.Dispose(); previous = (SD.Bitmap)frame.Clone();
            }
            return stitched?.Height ?? 0;
        }
        finally { stitched?.Dispose(); previous?.Dispose(); }
    }

    [Fact]
    public void StitchLoop_SlowScrollSmallRegion_ReconstructsFullHeight()
    {
        // The user's failure geometry: a narrow ~193×184 region scrolled slowly (~22px/frame).
        const int w = 193, h = 184, step = 22, docRows = 900;
        var frames = new List<SD.Bitmap>();
        for (int top = 0; top + h <= docRows; top += step)
            frames.Add(MakeViewport(top, w, h, header: 0));

        int finalHeight = RunStitchLoop(frames);
        try
        {
            // Every step must be detected and appended: first frame (h) + (n-1) steps of `step`.
            int expected = h + (frames.Count - 1) * step;
            Assert.Equal(expected, finalHeight);
        }
        finally { foreach (var f in frames) f.Dispose(); }
    }

    [Fact]
    public void StitchLoop_SlowScrollWithStickyHeader_ReconstructsAndIgnoresHeader()
    {
        // Same slow scroll, now with a 28px sticky header. With band detection removed, the
        // distinctive-run matcher must still lock onto the body (its overlap run dwarfs the
        // 28-row header), so the stitch grows by the body offset each frame — not stall at 0.
        const int w = 193, h = 184, header = 28, step = 22, docRows = 900;
        var frames = new List<SD.Bitmap>();
        for (int top = 0; top + (h - header) <= docRows; top += step)
            frames.Add(MakeViewport(top, w, h, header));

        int finalHeight = RunStitchLoop(frames);
        try
        {
            int expected = h + (frames.Count - 1) * step;
            Assert.Equal(expected, finalHeight);
        }
        finally { foreach (var f in frames) f.Dispose(); }
    }

    [Fact]
    public void FramesIdentical_TrueForSamePixels_FalseWhenContentDiffers()
    {
        using var a = MakeFrame(0);
        using var b = MakeFrame(0);
        using var c = MakeFrame(5);

        Assert.True(ImageStitcher.FramesIdentical(a, b));
        Assert.False(ImageStitcher.FramesIdentical(a, c));
    }
}
