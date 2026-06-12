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
}
