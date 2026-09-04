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

    [Fact]
    public void SampleAt_ClampsToTheBitmapInsteadOfThrowing()
    {
        using var bitmap = CreateBitmap(2, 2);
        bitmap.SetPixel(1, 1, SD.Color.FromArgb(255, 10, 20, 30));

        Assert.Equal(SD.Color.FromArgb(255, 10, 20, 30), EyedropperSampler.SampleAt(bitmap, new SD.Point(99, 99)));
    }

    [Fact]
    public void Average_IsTheMeanOfTheRegionNotOfTheWholeBitmap()
    {
        using var bitmap = CreateBitmap(4, 2);
        for (int y = 0; y < 2; y++)
        {
            bitmap.SetPixel(0, y, SD.Color.FromArgb(255, 0, 0, 0));
            bitmap.SetPixel(1, y, SD.Color.FromArgb(255, 100, 100, 100));
            bitmap.SetPixel(2, y, SD.Color.FromArgb(255, 255, 255, 255));
            bitmap.SetPixel(3, y, SD.Color.FromArgb(255, 255, 255, 255));
        }

        SD.Color average = EyedropperSampler.Average(bitmap, new SD.Rectangle(0, 0, 2, 2));

        Assert.Equal(50, average.R);
        Assert.Equal(50, average.G);
        Assert.Equal(50, average.B);
    }

    [Fact]
    public void Average_OfAnEmptyOrOffscreenRegionFallsBackToOnePixel()
    {
        using var bitmap = CreateBitmap(2, 2);
        bitmap.SetPixel(0, 0, SD.Color.FromArgb(255, 7, 8, 9));

        Assert.Equal(
            SD.Color.FromArgb(255, 7, 8, 9),
            EyedropperSampler.Average(bitmap, new SD.Rectangle(-50, -50, 4, 4)));
    }

    [Fact]
    public void Darkest_FindsTheTextStrokeWithoutLandingOnIt()
    {
        using var bitmap = CreateBitmap(20, 20);
        using (var g = SD.Graphics.FromImage(bitmap))
            g.Clear(SD.Color.White);
        // One dark glyph pixel, ten px away from where the pointer is.
        bitmap.SetPixel(4, 4, SD.Color.FromArgb(255, 20, 22, 24));

        SD.Color darkest = EyedropperSampler.Darkest(bitmap, new SD.Point(10, 10), box: 20);

        Assert.Equal(SD.Color.FromArgb(255, 20, 22, 24), darkest);
    }

    [Fact]
    public void Darkest_RanksByLuminanceSoASaturatedBlueDoesNotBeatAMidGrey()
    {
        using var bitmap = CreateBitmap(6, 6);
        using (var g = SD.Graphics.FromImage(bitmap))
            g.Clear(SD.Color.White);
        bitmap.SetPixel(2, 2, SD.Color.FromArgb(255, 0, 0, 255));   // channel sum 255
        bitmap.SetPixel(3, 3, SD.Color.FromArgb(255, 60, 60, 60));  // channel sum 180, but brighter

        SD.Color darkest = EyedropperSampler.Darkest(bitmap, new SD.Point(3, 3), box: 6);

        Assert.Equal(SD.Color.FromArgb(255, 0, 0, 255), darkest);
    }

    private static SD.Bitmap CreateBitmap(int width, int height)
    {
        var bitmap = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bitmap);
        g.Clear(SD.Color.Black);
        return bitmap;
    }
}
