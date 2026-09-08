using System.Drawing;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// A clipped repaint must draw the same picture the full repaint would — the follow tick
/// invalidates only the bands around the cursor, so anything the paint scribbles OUTSIDE the
/// region it was handed is left stale.
///
/// The bug these pin: BrightenRegion used <c>Graphics.SetClip(Rectangle)</c>, whose default
/// CombineMode is Replace, so the undimmed re-blit of the measured run threw away the update
/// region and covered the whole object; every crimson element drawn after it was then clipped
/// back to the band and never repainted the rest. Area and Window mode share that helper, so
/// the fix (IntersectClip) is guarded here for all three.
/// </summary>
public class MeasureClippedRepaintTests
{
    // A realistic monitor and a realistic object to measure.
    private static readonly Rectangle Screen = new(0, 0, 2560, 1440);
    private static readonly Rectangle Window = new(400, 300, 1400, 800);

    private static Bitmap Desktop()
    {
        var bmp = new Bitmap(Screen.Width, Screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0x10, 0x30, 0x80));
        using var pale = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0));
        g.FillRectangle(pale, Window);
        return bmp;
    }

    private static FastRegionSelectorDialog Selector() =>
        new(() => Task.FromResult(new List<WindowInfo>()), settings: null);

    /// <summary>Exactly what InvalidateFollowRegion asks for on an ordinary cursor step:
    /// two 32 px crosshair bands and a 500 px loupe box, at the old and new points.</summary>
    private static Region FollowRegion(Point from, Point to)
    {
        var region = new Region(Rectangle.Empty);
        void At(Point p)
        {
            region.Union(new Rectangle(p.X - 16, Screen.Top, 32, Screen.Height));
            region.Union(new Rectangle(Screen.Left, p.Y - 16, Screen.Width, 32));
            region.Union(new Rectangle(p.X - 250, p.Y - 250, 500, 500));
        }
        At(from);
        if (to != from) At(to);
        return region;
    }

    private static Bitmap Full(FastRegionSelectorDialog s)
    {
        var canvas = new Bitmap(Screen.Width, Screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        s.PaintSurface(g, Screen);
        return canvas;
    }

    [Fact]
    public void AClippedRepaintKeepsTheWholeDimensionHairline()
    {
        using var s = Selector();
        using Bitmap frozen = Desktop();
        var from = new Point(1000, 700);
        s.EnterMeasureForRender(frozen, Screen, from);
        Assert.Equal(Window, s.MeasuredScreenRect);

        // Frame 1: the whole surface, as it looks after the mode opens.
        using Bitmap frame = Full(s);

        // Frame 2: the pointer moves one pixel. The reading does not change, so the only
        // thing invalidated is the follow band around the cursor.
        var to = new Point(1001, 700);
        s.MoveMeasureCursorForRender(to);
        Assert.Equal(Window, s.MeasuredScreenRect);
        using (Region region = FollowRegion(from, to))
        using (var g = Graphics.FromImage(frame))
        {
            g.Clip = region;
            s.PaintSurface(g, Screen);
        }

        using Bitmap truth = Full(s);

        // Count crimson pixels on the horizontal dimension's own row, far from the cursor.
        int row = Math.Clamp(to.Y, Window.Top, Window.Bottom - 1);
        int lostLeft = 0, lostRight = 0;
        for (int x = Window.Left; x < Window.Right; x++)
        {
            bool truthInk = IsMeasureInk(truth.GetPixel(x, row));
            bool frameInk = IsMeasureInk(frame.GetPixel(x, row));
            if (truthInk && !frameInk)
            {
                if (x < to.X) lostLeft++; else lostRight++;
            }
        }

        Assert.True(lostLeft == 0 && lostRight == 0,
            $"the dimension hairline lost {lostLeft} px to the left and {lostRight} px to the right " +
            $"of the cursor after a one-pixel move inside the same object");
    }

    [Fact]
    public void AClippedRepaintKeepsAHeldSpanThatCrossesTheMeasuredObject()
    {
        using var s = Selector();
        using Bitmap frozen = Desktop();
        s.EnterMeasureForRender(frozen, Screen, new Point(1000, 400));
        s.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.H));   // hold the window's width at y=400
        // Walk far away from the hold, then take the "before" frame there.
        var from = new Point(1000, 900);
        s.MoveMeasureCursorForRender(from);
        using Bitmap frame = Full(s);

        var to = new Point(1000, 901);   // one pixel, still inside the same window
        s.MoveMeasureCursorForRender(to);
        using (Region region = FollowRegion(from, to))
        using (var g = Graphics.FromImage(frame))
        {
            g.Clip = region;
            s.PaintSurface(g, Screen);
        }
        using Bitmap truth = Full(s);

        int lost = 0;
        for (int x = Window.Left; x < Window.Right; x++)
            if (IsMeasureInk(truth.GetPixel(x, 400)) && !IsMeasureInk(frame.GetPixel(x, 400)))
                lost++;

        Assert.True(lost == 0, $"the held span lost {lost} px of its hairline to one cursor step");
    }


    [Fact]
    public void AClippedRepaintKeepsAGuideWhereItCrossesTheMeasuredObject()
    {
        using var s = Selector();
        using Bitmap frozen = Desktop();
        var from = new Point(1600, 900);
        s.EnterMeasureForRender(frozen, Screen, from);
        s.MeasureState.AddGuideX(600);          // a vertical guide crossing the window
        s.MoveMeasureCursorForRender(from);
        using Bitmap frame = Full(s);

        var to = new Point(1601, 900);
        s.MoveMeasureCursorForRender(to);
        using (Region region = FollowRegion(from, to))
        using (var g = Graphics.FromImage(frame))
        {
            g.Clip = region;
            s.PaintSurface(g, Screen);
        }
        using Bitmap truth = Full(s);

        int lost = 0;
        for (int y = Window.Top; y < Window.Bottom; y++)
            if (truth.GetPixel(600, y).ToArgb() != frame.GetPixel(600, y).ToArgb())
                lost++;
        Assert.True(lost == 0, $"the guide lost {lost} px where it crosses the measured object");
    }

    private static bool IsMeasureInk(Color c) =>
        // crimson E0244A, or the near-black halo the hairline sits on
        (c.R > 150 && c.G < 110 && c.B < 130) || (c.R < 90 && c.G < 90 && c.B < 90);
}
