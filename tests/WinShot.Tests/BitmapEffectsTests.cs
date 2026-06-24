using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class BitmapEffectsTests
{
    [Fact]
    public void PixelateRandomized_ReplaysSameSeedByteIdentically()
    {
        using var first = CreateGradientBitmap(24, 18);
        using var second = CreateGradientBitmap(24, 18);
        var region = new SD.Rectangle(3, 2, 16, 12);

        BitmapEffects.PixelateRandomized(first, region, seed: 12345, cellSize: 4);
        BitmapEffects.PixelateRandomized(second, region, seed: 12345, cellSize: 4);

        AssertBitmapsEqual(first, second);
    }

    [Fact]
    public void PixelateRandomized_OnlyChangesRequestedRegion()
    {
        using var original = CreateGradientBitmap(24, 18);
        using var edited = CreateGradientBitmap(24, 18);
        var region = new SD.Rectangle(4, 3, 12, 10);

        BitmapEffects.PixelateRandomized(edited, region, seed: 77, cellSize: 3);

        int changedInside = 0;
        for (int y = 0; y < edited.Height; y++)
        {
            for (int x = 0; x < edited.Width; x++)
            {
                bool inside = region.Contains(x, y);
                bool same = edited.GetPixel(x, y).ToArgb() == original.GetPixel(x, y).ToArgb();

                if (inside && !same)
                    changedInside++;
                else if (!inside)
                    Assert.True(same, $"Pixel outside region changed at {x},{y}.");
            }
        }

        Assert.True(changedInside > 0);
    }

    [Fact]
    public void RestoreRegion_RestoresPixelatedPixels()
    {
        using var bitmap = CreateGradientBitmap(24, 18);
        var region = new SD.Rectangle(4, 3, 12, 10);
        using var backup = bitmap.Clone(region, bitmap.PixelFormat);

        BitmapEffects.PixelateRandomized(bitmap, region, seed: 99, cellSize: 3);
        BitmapEffects.RestoreRegion(bitmap, backup, region);

        using var original = CreateGradientBitmap(24, 18);
        AssertBitmapsEqual(original, bitmap);
    }

    private static SD.Bitmap CreateGradientBitmap(int width, int height)
    {
        var bitmap = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, SD.Color.FromArgb(255, (x * 13) % 256, (y * 17) % 256, (x * 7 + y * 5) % 256));
        return bitmap;
    }

    private static void AssertBitmapsEqual(SD.Bitmap expected, SD.Bitmap actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.Equal(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
    }
}
