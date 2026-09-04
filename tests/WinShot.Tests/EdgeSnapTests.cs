using System.Drawing;
using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

public class EdgeSnapTests
{
    /// <summary>A blue frame with one solid green square, the shape the research file pins:
    /// 5x5 green at (20,100) means a cursor inside it must read back exactly 5 x 5.</summary>
    private static Bitmap Scene(int width = 200, int height = 200)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Blue);
        using var green = new SolidBrush(Color.Lime);
        g.FillRectangle(green, 20, 100, 5, 5);
        return bmp;
    }

    [Fact]
    public void MeasuresASolidRunInclusively()
    {
        using var bmp = Scene();
        using var snap = EdgeSnap.Lock(bmp);

        Rectangle rect = snap.DetectEdges(new Point(22, 102), EdgeSnap.DefaultTolerance);

        Assert.Equal(new Rectangle(20, 100, 5, 5), rect);
    }

    [Fact]
    public void EveryPixelOfTheRunAgreesOnTheSameBounds()
    {
        using var bmp = Scene();
        using var snap = EdgeSnap.Lock(bmp);

        for (int y = 100; y < 105; y++)
        {
            for (int x = 20; x < 25; x++)
                Assert.Equal(new Rectangle(20, 100, 5, 5), snap.DetectEdges(new Point(x, y), EdgeSnap.DefaultTolerance));
        }
    }

    [Fact]
    public void TheFourRaysAreIndependentSoAGapMeasuresAsAGap()
    {
        // Two green bars with a 30 px blue gutter between them. Hovering the gutter measures
        // the gutter horizontally and the full frame vertically — nobody had to click anything.
        using var bmp = new Bitmap(200, 200, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Blue);
            using var green = new SolidBrush(Color.Lime);
            g.FillRectangle(green, 0, 0, 40, 200);
            g.FillRectangle(green, 70, 0, 40, 200);
        }
        using var snap = EdgeSnap.Lock(bmp);

        Rectangle rect = snap.DetectEdges(new Point(55, 100), EdgeSnap.DefaultTolerance);

        Assert.Equal(40, rect.Left);
        Assert.Equal(30, rect.Width);
        Assert.Equal(0, rect.Top);
        Assert.Equal(200, rect.Height);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(199, 199)]
    [InlineData(0, 199)]
    [InlineData(199, 0)]
    public void CornersClampInsteadOfReadingOutOfBounds(int x, int y)
    {
        using var bmp = Scene();
        using var snap = EdgeSnap.Lock(bmp);

        Rectangle rect = snap.DetectEdges(new Point(x, y), EdgeSnap.DefaultTolerance);

        Assert.True(rect.Width > 0 && rect.Height > 0);
        Assert.True(new Rectangle(0, 0, 200, 200).Contains(rect));
    }

    [Fact]
    public void MaximumToleranceSwallowsEveryEdgeAndReturnsTheWholeFrame()
    {
        using var bmp = Scene();
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(new Rectangle(0, 0, 200, 200), snap.DetectEdges(new Point(22, 102), 255));
    }

    [Fact]
    public void ZeroToleranceStillHoldsASolidRunTogether()
    {
        using var bmp = Scene();
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(new Rectangle(20, 100, 5, 5), snap.DetectEdges(new Point(22, 102), 0));
    }

    [Fact]
    public void ABitmapTooThinToWalkIsOneRun()
    {
        using var bmp = new Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(new Rectangle(0, 0, 2, 2), snap.DetectEdges(new Point(1, 1), EdgeSnap.DefaultTolerance));
    }

    [Fact]
    public void ToleranceDecidesWhetherAHairlineSeparatorStopsTheWalk()
    {
        // A near-black line one shade off the black around it: invisible at the tuned default,
        // a hard stop at zero. This is the whole reason the wheel knob exists.
        using var bmp = new Bitmap(100, 100, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0, 0, 0));
            using var hairline = new SolidBrush(Color.FromArgb(6, 6, 6));
            g.FillRectangle(hairline, 0, 50, 100, 1);
        }
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(100, snap.DetectEdges(new Point(50, 20), EdgeSnap.DefaultTolerance).Height);
        Assert.Equal(50, snap.DetectEdges(new Point(50, 20), 0).Height);
    }

    [Fact]
    public void PerChannelIsStricterThanTheSummedMetric()
    {
        // One channel off by 20, the rest identical: summed L1 = 20 (close at tolerance 30),
        // per-channel with tolerance 10 = not close.
        uint a = 0xFF_20_20_20;
        uint b = 0xFF_20_20_34;

        Assert.True(EdgeSnap.PixelsClose(a, b, 30, perChannel: false));
        Assert.False(EdgeSnap.PixelsClose(a, b, 10, perChannel: true));
        Assert.True(EdgeSnap.PixelsClose(a, b, 20, perChannel: true));
    }

    [Fact]
    public void AHighContrastPairIsFarApartRatherThanWrappingIntoLookingIdentical()
    {
        // Blue on white: 224 + 192 + 112 = 528. PowerToys masks that sum to a byte, which
        // wraps it to 16 and calls the two colours the same thing. Clamping is the one
        // deliberate deviation from the port, and this is the case it exists for.
        uint blue = 0xFF_10_30_80;
        uint white = 0xFF_F0_F0_F0;

        Assert.False(EdgeSnap.PixelsClose(blue, white, 30, perChannel: false));
        Assert.False(EdgeSnap.PixelsClose(blue, white, 254, perChannel: false));
        Assert.True(EdgeSnap.PixelsClose(blue, white, 255, perChannel: false));
    }

    [Fact]
    public void ASolidCardOnAContrastingDesktopMeasuresAsTheCard()
    {
        using var bmp = new Bitmap(400, 300, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0x10, 0x30, 0x80));
            using var card = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0));
            g.FillRectangle(card, 50, 40, 100, 60);
        }
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(new Rectangle(50, 40, 100, 60), snap.DetectEdges(new Point(80, 60), EdgeSnap.DefaultTolerance));
    }

    [Fact]
    public void ReadingAPixelOutsideTheBitmapIsNotACrash()
    {
        using var bmp = Scene();
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(0u, snap.PixelAt(-1, 0));
        Assert.Equal(0u, snap.PixelAt(0, 200));
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        using var bmp = Scene();
        var snap = EdgeSnap.Lock(bmp);
        snap.Dispose();
        snap.Dispose();

        // A disposed view answers rather than throwing, so a late follow-tick cannot crash.
        Assert.Equal(new Rectangle(0, 0, 200, 200), snap.DetectEdges(new Point(22, 102), 30));
    }
}
