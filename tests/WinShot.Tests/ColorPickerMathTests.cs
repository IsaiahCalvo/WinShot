using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class ColorPickerMathTests
{
    [Theory]
    [InlineData(255, 0, 0, 0)]
    [InlineData(0, 255, 0, 120)]
    [InlineData(0, 0, 255, 240)]
    [InlineData(255, 255, 0, 60)]
    [InlineData(0, 255, 255, 180)]
    [InlineData(255, 0, 255, 300)]
    public void PrimariesAndSecondariesLandOnTheirHue(byte r, byte g, byte b, double hue)
    {
        var (h, s, v) = ColorPickerMath.ToHsv(Color.FromRgb(r, g, b));
        Assert.Equal(hue, h, 3);
        Assert.Equal(1, s, 3);
        Assert.Equal(1, v, 3);
    }

    [Fact]
    public void GreysHaveNoHueOrSaturation()
    {
        var (_, s, v) = ColorPickerMath.ToHsv(Color.FromRgb(128, 128, 128));
        Assert.Equal(0, s, 3);
        Assert.Equal(128 / 255d, v, 3);

        var (_, blackS, blackV) = ColorPickerMath.ToHsv(Colors.Black);
        Assert.Equal(0, blackS, 3);
        Assert.Equal(0, blackV, 3);
    }

    /// <summary>
    /// The square and the sliders write into HSV and read back out of it on every drag, so
    /// a colour that does not survive the round trip drifts while the user is holding the
    /// mouse down.
    /// </summary>
    [Fact]
    public void EveryColourSurvivesTheRoundTrip()
    {
        var rng = new Random(20260904);
        for (int i = 0; i < 400; i++)
        {
            var original = Color.FromArgb(
                (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            var (h, s, v) = ColorPickerMath.ToHsv(original);
            Color round = ColorPickerMath.FromHsv(h, s, v, original.A);
            Assert.Equal(original, round);
        }
    }

    [Fact]
    public void HueWrapsAndSaturationValueClamp()
    {
        Assert.Equal(ColorPickerMath.FromHsv(0, 1, 1), ColorPickerMath.FromHsv(360, 1, 1));
        Assert.Equal(ColorPickerMath.FromHsv(10, 1, 1), ColorPickerMath.FromHsv(370, 1, 1));
        Assert.Equal(ColorPickerMath.FromHsv(10, 1, 1), ColorPickerMath.FromHsv(-350, 1, 1));
        Assert.Equal(ColorPickerMath.FromHsv(200, 1, 1), ColorPickerMath.FromHsv(200, 5, 5));
        Assert.Equal(ColorPickerMath.FromHsv(200, 0, 0), ColorPickerMath.FromHsv(200, -1, -1));
    }

    /// <summary>Saturation runs left to right and value runs BOTTOM to top — white is top-left.</summary>
    [Fact]
    public void TheSquareIsWhiteTopLeftAndBlackAlongTheBottom()
    {
        var canvas = new Size(200, 100);

        Assert.Equal(new Point(0, 0), ColorPickerMath.PositionFor(0, 1, canvas));
        Assert.Equal(new Point(200, 0), ColorPickerMath.PositionFor(1, 1, canvas));
        Assert.Equal(new Point(0, 100), ColorPickerMath.PositionFor(0, 0, canvas));

        var (s, v) = ColorPickerMath.FromPosition(new Point(100, 25), canvas);
        Assert.Equal(0.5, s, 6);
        Assert.Equal(0.75, v, 6);
    }

    [Fact]
    public void APointerOutsideTheSquareClampsIntoIt()
    {
        var canvas = new Size(200, 100);
        var (s, v) = ColorPickerMath.FromPosition(new Point(-40, 400), canvas);
        Assert.Equal(0, s, 6);
        Assert.Equal(0, v, 6);

        (s, v) = ColorPickerMath.FromPosition(new Point(9999, -9999), canvas);
        Assert.Equal(1, s, 6);
        Assert.Equal(1, v, 6);
    }

    [Fact]
    public void HexOnlyCarriesAlphaWhenThereIsAlphaToCarry()
    {
        Assert.Equal("#FF3B30", ColorPickerMath.ToHex(Color.FromRgb(0xFF, 0x3B, 0x30)));
        Assert.Equal("#80FF3B30", ColorPickerMath.ToHex(Color.FromArgb(0x80, 0xFF, 0x3B, 0x30)));
    }

    /// <summary>
    /// "My colours" is most-recent-first, capped, and re-saving a colour it already holds
    /// moves it to the front rather than filling the row with duplicates.
    /// </summary>
    [Fact]
    public void SavedColoursAreMostRecentFirstDeduplicatedAndCapped()
    {
        SavedColors.ClearForTest();
        try
        {
            for (int i = 0; i < SavedColors.Capacity + 3; i++)
                SavedColors.Add(Color.FromRgb((byte)i, 0, 0));

            Assert.Equal(SavedColors.Capacity, SavedColors.All.Count);
            Assert.Equal(Color.FromRgb((byte)(SavedColors.Capacity + 2), 0, 0), SavedColors.All[0]);

            Color oldest = SavedColors.All[^1];
            SavedColors.Add(oldest);
            Assert.Equal(oldest, SavedColors.All[0]);
            Assert.Equal(SavedColors.Capacity, SavedColors.All.Count);
            Assert.Single(SavedColors.All, c => c == oldest);

            SavedColors.Remove(oldest);
            Assert.DoesNotContain(oldest, SavedColors.All);
        }
        finally
        {
            SavedColors.ClearForTest();
        }
    }
}
