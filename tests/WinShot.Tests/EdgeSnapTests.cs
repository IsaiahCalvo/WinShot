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

    // ------------------------------------------------ the port stops where the port stops

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + ((b.R - a.R) * t)),
        (int)Math.Round(a.G + ((b.G - a.G) * t)),
        (int)Math.Round(a.B + ((b.B - a.B) * t)));

    [Fact]
    public void AnAntiAliasedStrokeMeasuresItsSolidCoreAndNothingMore()
    {
        // A 1 px ink stroke with one blended column either side. The ray stops at the first
        // pixel that is not the colour it started on, which is the fringe, and returns the
        // last similar coordinate — so the answer is 1, exactly as PowerToys returns.
        //
        // This is deliberate, not an oversight. §6 of the sweep report closes the door on
        // anti-aliasing hysteresis until the shipped tolerance knob has proved insufficient
        // in a user's hands, and a peek past the stop would also break §3.3's two rules: the
        // comparison is against the FIXED cursor pixel, and the edge is the last similar
        // coordinate. The knob is the answer here — at tolerance 0 the fringe still stops it.
        var bmp = new Bitmap(60, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Color ink = Color.FromArgb(0x20, 0x20, 0x20);
        Color paper = Color.White;
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(paper);
            using var brush = new SolidBrush(ink);
            g.FillRectangle(brush, 30, 0, 1, 40);
        }
        for (int y = 0; y < 40; y++)
        {
            bmp.SetPixel(29, y, Blend(ink, paper, 0.5));
            bmp.SetPixel(31, y, Blend(ink, paper, 0.5));
        }
        using (bmp)
        {
            using var snap = EdgeSnap.Lock(bmp);

            Rectangle rect = snap.DetectEdges(new Point(30, 20), EdgeSnap.DefaultTolerance);

            Assert.Equal(30, rect.Left);
            Assert.Equal(1, rect.Width);
        }
    }

    [Fact]
    public void AHairlineBetweenTwoPatchesOfOneColourIsAnEdge()
    {
        // A 1 px window border or table rule between two patches of the same colour: the
        // walk stops at the border, so a bordered card still measures its border.
        using var bmp = new Bitmap(60, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0xF0, 0xF0, 0xF0));
            using var rule = new SolidBrush(Color.FromArgb(0x60, 0x60, 0x60));
            g.FillRectangle(rule, 30, 0, 1, 40);
        }
        using var snap = EdgeSnap.Lock(bmp);

        Rectangle rect = snap.DetectEdges(new Point(20, 20), EdgeSnap.DefaultTolerance);

        Assert.Equal(30, rect.Right);
    }

    [Fact]
    public void AGradientIsWalkedAgainstTheStartPixelRatherThanTheNeighbour()
    {
        // Every column of a ramp is close to the one beside it, so a neighbour-to-neighbour
        // walk would swallow the whole screen. Against the fixed start pixel it stops after
        // as many steps as the tolerance buys: 2 per channel, 6 summed, five steps each way.
        using var bmp = new Bitmap(120, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            for (int x = 0; x < 120; x++)
            {
                using var brush = new SolidBrush(Color.FromArgb(x * 2, x * 2, x * 2));
                g.FillRectangle(brush, x, 0, 1, 40);
            }
        }
        using var snap = EdgeSnap.Lock(bmp);

        Rectangle rect = snap.DetectEdges(new Point(60, 20), EdgeSnap.DefaultTolerance);

        Assert.Equal(55, rect.Left);
        Assert.Equal(11, rect.Width);
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

    [Fact]
    public void ARunWhoseNeighbourIsTheFirstColumnOrRowIsNotSwallowedWhole()
    {
        // The low-side rays used to stop one step early — index 0 was returned as the edge
        // without ever being compared — so any object sitting against the first column or
        // row of the virtual desktop measured one background pixel too wide.
        using var bmp = new Bitmap(40, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, new Rectangle(1, 1, 10, 10));
        }
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(
            new Rectangle(1, 1, 10, 10),
            snap.DetectEdges(new Point(5, 5), EdgeSnap.DefaultTolerance));
    }

    [Fact]
    public void TheLowAndHighRaysAgreeAboutTheLastPixelTheyTest()
    {
        // The mirror of the case above, and the reason it was a bug rather than a choice:
        // the high side has always compared index maxDim-1, so the low side must compare 0.
        using var bmp = new Bitmap(40, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, new Rectangle(29, 29, 10, 10));
        }
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(
            new Rectangle(29, 29, 10, 10),
            snap.DetectEdges(new Point(33, 33), EdgeSnap.DefaultTolerance));
    }

    [Fact]
    public void ARunTouchingTheFrameStillDegradesToItRatherThanFailing()
    {
        // Nothing but the object in column/row 0 to stop on: the ray runs off the frame and
        // degrades to it rather than failing.
        using var bmp = new Bitmap(40, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, new Rectangle(0, 0, 10, 10));
        }
        using var snap = EdgeSnap.Lock(bmp);

        Assert.Equal(
            new Rectangle(0, 0, 10, 10),
            snap.DetectEdges(new Point(5, 5), EdgeSnap.DefaultTolerance));
    }
}
