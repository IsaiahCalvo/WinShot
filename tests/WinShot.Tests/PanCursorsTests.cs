using System.IO;
using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The editor's grab / grabbing hands are drawn at runtime per monitor scale. These scan the
/// ink the way SelectorRenderHarness scans the camera: nothing clipped, filled, and growing
/// with the scale.
/// </summary>
public class PanCursorsTests
{
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.0, true)]
    [InlineData(1.25, false)]
    [InlineData(1.5, false)]
    [InlineData(1.5, true)]
    [InlineData(2.0, false)]
    [InlineData(2.0, true)]
    public void HandDrawsInsideItsBitmapAtEveryScale(double scale, bool closed)
    {
        using SD.Bitmap bmp = PanCursors.BuildHandBitmap(scale, closed);
        SD.Rectangle ink = InkBounds(bmp);
        Assert.False(ink.IsEmpty, $"the hand drew nothing at {scale:0.##}x closed={closed}");
        Assert.True(ink.Left > 0 && ink.Top > 0 && ink.Right < bmp.Width && ink.Bottom < bmp.Height,
            $"hand touches the bitmap edge at {scale:0.##}x closed={closed}: ink {ink} in {bmp.Width}x{bmp.Height}");
        // A row through the palm is solid: fill, not a ring of capsule outlines.
        int row = ink.Top + (int)Math.Round(ink.Height * 0.8);
        int holes = 0; bool seen = false;
        for (int x = ink.Left; x < ink.Right; x++)
        {
            bool inkHere = bmp.GetPixel(x, row).A > 8;
            if (seen && !inkHere && x < ink.Right - 1 && bmp.GetPixel(x + 1, row).A > 8) holes++;
            seen |= inkHere;
        }
        Assert.Equal(0, holes);
    }

    /// <summary>With WINSHOT_RENDER_OUT set, writes pan-cursors.png: every hand at 1/1.5/2x, 2x magnified.</summary>
    [Fact]
    public void DumpContactSheet()
    {
        string? dir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        using var sheet = new SD.Bitmap(560, 150);
        using var g = SD.Graphics.FromImage(sheet);
        g.Clear(SD.Color.FromArgb(255, 90, 90, 90));
        g.InterpolationMode = SD.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.Half;
        int x = 4;
        foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            foreach (bool closed in new[] { false, true })
            {
                using SD.Bitmap b = PanCursors.BuildHandBitmap(scale, closed);
                g.DrawImage(b, x, 4, b.Width * 2, b.Height * 2);
                x += (b.Width * 2) + 6;
            }
        sheet.Save(Path.Combine(dir, "pan-cursors.png"));
    }

    [Fact]
    public void HandGrowsWithTheMonitorScale()
    {
        using SD.Bitmap one = PanCursors.BuildHandBitmap(1.0, false);
        using SD.Bitmap two = PanCursors.BuildHandBitmap(2.0, false);
        Assert.Equal(one.Width * 2, two.Width);
        Assert.InRange(InkBounds(two).Width, InkBounds(one).Width * 2 - 2, InkBounds(one).Width * 2 + 2);
    }

    private static SD.Rectangle InkBounds(SD.Bitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).A > 8)
                { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        return maxX < 0 ? SD.Rectangle.Empty : SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}
