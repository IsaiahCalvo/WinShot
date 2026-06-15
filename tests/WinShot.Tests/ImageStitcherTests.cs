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
}
