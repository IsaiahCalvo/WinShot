using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class EyedropperSamplerTests
{
    [Fact]
    public void SampleClamped_FloorsAndClampsToBitmapPixels()
    {
        using var bitmap = CreateBitmap(2, 2);
        bitmap.SetPixel(1, 0, SD.Color.FromArgb(255, 12, 34, 56));
        bitmap.SetPixel(0, 1, SD.Color.FromArgb(255, 90, 80, 70));

        Assert.Equal(Color.FromRgb(12, 34, 56), EyedropperSampler.SampleClamped(bitmap, new Point(1.9, 0.2)));
        Assert.Equal(Color.FromRgb(90, 80, 70), EyedropperSampler.SampleClamped(bitmap, new Point(-5, 99)));
    }

    [Fact]
    public void Preview_HidesWhenPointerIsOutsideBitmap()
    {
        using var bitmap = CreateBitmap(10, 10);

        var preview = EyedropperSampler.Preview(bitmap, new Point(10, 5), zoom: 1);

        Assert.False(preview.Visible);
    }

    [Fact]
    public void Preview_FormatsHexAndKeepsScreenSizedOffsetAtZoom()
    {
        using var bitmap = CreateBitmap(200, 100);
        bitmap.SetPixel(2, 1, SD.Color.FromArgb(255, 12, 34, 56));

        var preview = EyedropperSampler.Preview(bitmap, new Point(2.2, 1.8), zoom: 2);

        Assert.True(preview.Visible);
        Assert.Equal(Color.FromRgb(12, 34, 56), preview.Color);
        Assert.Equal("#0C2238", preview.Hex);
        Assert.Equal(0.5, preview.Scale);
        Assert.Equal(10.2, preview.Left);
        Assert.Equal(9.8, preview.Top);
    }

    [Fact]
    public void Preview_StaysInsideBitmapNearRightAndBottomEdges()
    {
        using var bitmap = CreateBitmap(100, 50);

        var preview = EyedropperSampler.Preview(bitmap, new Point(99, 49), zoom: 1);

        Assert.True(preview.Visible);
        Assert.Equal(4, preview.Left);
        Assert.Equal(5, preview.Top);
    }

    private static SD.Bitmap CreateBitmap(int width, int height)
    {
        var bitmap = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bitmap);
        g.Clear(SD.Color.Black);
        return bitmap;
    }
}
