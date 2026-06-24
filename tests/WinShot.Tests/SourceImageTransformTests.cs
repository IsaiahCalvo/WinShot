using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class SourceImageTransformTests
{
    [Fact]
    public void RotateFlip_RotatesClockwiseWithoutMutatingSource()
    {
        using var source = CreateLabeledBitmap();

        using var rotated = SourceImageTransform.RotateFlip(source, SD.RotateFlipType.Rotate90FlipNone);

        Assert.Equal(3, rotated.Width);
        Assert.Equal(2, rotated.Height);
        AssertPixelGrid(rotated, "ECA", "FDB");
        AssertPixelGrid(source, "AB", "CD", "EF");
    }

    [Fact]
    public void RotateFlip_RotatesCounterClockwise()
    {
        using var source = CreateLabeledBitmap();

        using var rotated = SourceImageTransform.RotateFlip(source, SD.RotateFlipType.Rotate270FlipNone);

        Assert.Equal(3, rotated.Width);
        Assert.Equal(2, rotated.Height);
        AssertPixelGrid(rotated, "BDF", "ACE");
    }

    [Fact]
    public void RotateFlip_FlipsHorizontally()
    {
        using var source = CreateLabeledBitmap();

        using var flipped = SourceImageTransform.RotateFlip(source, SD.RotateFlipType.RotateNoneFlipX);

        Assert.Equal(2, flipped.Width);
        Assert.Equal(3, flipped.Height);
        AssertPixelGrid(flipped, "BA", "DC", "FE");
    }

    [Fact]
    public void RotateFlip_FlipsVertically()
    {
        using var source = CreateLabeledBitmap();

        using var flipped = SourceImageTransform.RotateFlip(source, SD.RotateFlipType.RotateNoneFlipY);

        Assert.Equal(2, flipped.Width);
        Assert.Equal(3, flipped.Height);
        AssertPixelGrid(flipped, "EF", "CD", "AB");
    }

    private static SD.Bitmap CreateLabeledBitmap()
    {
        var bitmap = new SD.Bitmap(2, 3, SD.Imaging.PixelFormat.Format32bppArgb);
        SetLabel(bitmap, 0, 0, 'A');
        SetLabel(bitmap, 1, 0, 'B');
        SetLabel(bitmap, 0, 1, 'C');
        SetLabel(bitmap, 1, 1, 'D');
        SetLabel(bitmap, 0, 2, 'E');
        SetLabel(bitmap, 1, 2, 'F');
        return bitmap;
    }

    private static void AssertPixelGrid(SD.Bitmap bitmap, params string[] rows)
    {
        Assert.Equal(rows.Length, bitmap.Height);
        Assert.Equal(rows[0].Length, bitmap.Width);
        for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < rows[y].Length; x++)
                Assert.Equal(ColorFor(rows[y][x]).ToArgb(), bitmap.GetPixel(x, y).ToArgb());
    }

    private static void SetLabel(SD.Bitmap bitmap, int x, int y, char label) =>
        bitmap.SetPixel(x, y, ColorFor(label));

    private static SD.Color ColorFor(char label) => label switch
    {
        'A' => SD.Color.FromArgb(255, 10, 0, 0),
        'B' => SD.Color.FromArgb(255, 20, 0, 0),
        'C' => SD.Color.FromArgb(255, 30, 0, 0),
        'D' => SD.Color.FromArgb(255, 40, 0, 0),
        'E' => SD.Color.FromArgb(255, 50, 0, 0),
        'F' => SD.Color.FromArgb(255, 60, 0, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, null),
    };
}
