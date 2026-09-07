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

    // ------------------------------------------------------ anti-aliasing and confidence

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + ((b.R - a.R) * t)),
        (int)Math.Round(a.G + ((b.G - a.G) * t)),
        (int)Math.Round(a.B + ((b.B - a.B) * t)));

    /// <summary>One vertical stroke: a solid core column with one blended column on each
    /// side, which is what a 3 px anti-aliased letterform stem actually looks like.</summary>
    private static Bitmap Stem(int core = 1)
    {
        var bmp = new Bitmap(60, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Color ink = Color.FromArgb(0x20, 0x20, 0x20);
        Color paper = Color.White;
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(paper);
            using var brush = new SolidBrush(ink);
            g.FillRectangle(brush, 30, 0, core, 40);
        }
        for (int y = 0; y < 40; y++)
        {
            bmp.SetPixel(29, y, Blend(ink, paper, 0.5));
            bmp.SetPixel(30 + core, y, Blend(ink, paper, 0.5));
        }
        return bmp;
    }

    [Fact]
    public void AnAntiAliasedStrokeMeasuresTheWidthTheEyeSees()
    {
        // Stopping at the first blended pixel reports a 1 px stroke for something plainly
        // three across. Both fringes are a channel-wise blend of the ink and the paper, so
        // the walk crosses them and stops where the paper actually begins.
        using Bitmap bmp = Stem();
        using var snap = EdgeSnap.Lock(bmp);

        Rectangle rect = snap.DetectEdges(new Point(30, 20), EdgeSnap.DefaultTolerance);

        Assert.Equal(29, rect.Left);
        Assert.Equal(3, rect.Width);
    }

    [Fact]
    public void AHairlineBetweenTwoPatchesOfOneColourIsStillAnEdge()
    {
        // The guard on the anti-aliasing walk. A 1 px window border or table rule sits
        // between two patches of the SAME colour, so it is a blend of nothing: bridging it
        // would make every bordered card measure as the thing that contains it.
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
    public void AHardEdgeIsFirmAndAFrameEdgeIsNot()
    {
        using var bmp = Scene();
        using var snap = EdgeSnap.Lock(bmp);

        EdgeRun run = snap.DetectRun(new Point(22, 102), EdgeSnap.DefaultTolerance);
        Assert.Equal(new Rectangle(20, 100, 5, 5), run.Bounds);
        Assert.True(run.LeftFirm && run.RightFirm && run.TopFirm && run.BottomFirm);

        // Out on the blue, every ray runs off the frame: there is no edge there to be sure of.
        EdgeRun open = snap.DetectRun(new Point(150, 20), EdgeSnap.DefaultTolerance);
        Assert.False(open.LeftFirm || open.RightFirm || open.TopFirm || open.BottomFirm);
    }

    [Fact]
    public void AnEdgeThatOnlyJustClearedTheToleranceIsNotFirm()
    {
        // 36 away on the summed metric: past the default 30, nowhere near decisive. This is
        // the reading the HUD must not dress up as a fact.
        using var bmp = new Bitmap(60, 40, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0x10, 0x30, 0x80));
            using var faint = new SolidBrush(Color.FromArgb(0x1C, 0x3C, 0x8C));
            g.FillRectangle(faint, 20, 10, 20, 20);
        }
        using var snap = EdgeSnap.Lock(bmp);

        EdgeRun run = snap.DetectRun(new Point(30, 20), EdgeSnap.DefaultTolerance);

        Assert.Equal(new Rectangle(20, 10, 20, 20), run.Bounds);
        Assert.False(run.LeftFirm || run.RightFirm || run.TopFirm || run.BottomFirm);
    }

    [Fact]
    public void TheAntiAliasWalkNeverReachesFurtherThanItsPeek()
    {
        // A long ramp is a gradient, not a fringe, and a gradient's own neighbours are close
        // to each other — so the walk settles immediately and the run ends where it did.
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

        // Six columns each side at 2/channel: the ramp itself, no fringe, no peek.
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
}
