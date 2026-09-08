using SD = System.Drawing;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The trim is the literal "trim window shadow": on Windows the only shadow inside a window
/// capture is the wedge outside each rounded corner, so the test asks what happens to a
/// corner, to the middle, and to the pixels the arc grazes.
/// </summary>
public class WindowShadowTrimTests
{
    /// <summary>An opaque capture — every pixel a distinct, fully opaque colour.</summary>
    private static SD.Bitmap Capture(int w = 80, int h = 60)
    {
        var bmp = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.FromArgb(255, 240, 0, 120));
        return bmp;
    }

    /// <summary>
    /// The corner a trim cuts is ARGB(0,0,0,0) — transparent BLACK, because that is what
    /// GDI+ leaves in a bitmap it never painted. Invisible wherever alpha is honoured, solid
    /// black wherever it is not: a JPEG save and the clipboard's CF_DIB payload both read
    /// the RGB underneath. With the trim defaulting on, that was four black wedges on every
    /// Windows 11 window capture saved as JPG or pasted into MS Paint.
    /// </summary>
    [Fact]
    public void ATrimmedCornerSavedAsJpegIsWhite_NotBlack()
    {
        using SD.Bitmap shot = Capture(200, 160);
        using SD.Bitmap trimmed = WindowShadowTrim.Trim(shot, radius: 24)!;
        Assert.Equal(0, trimmed.GetPixel(0, 0).A);

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"winshot-trim-{Guid.NewGuid():N}.jpg");
        try
        {
            ImageSaver.Save(trimmed, path);
            using var reloaded = new SD.Bitmap(path);
            SD.Color corner = reloaded.GetPixel(1, 1);
            SD.Color middle = reloaded.GetPixel(reloaded.Width / 2, reloaded.Height / 2);
            Assert.True(corner.R > 215 && corner.G > 215 && corner.B > 215,
                $"the trimmed corner encoded as {corner}, not white");
            // And the window itself is untouched by the flatten.
            Assert.True(middle.R > 215 && middle.G < 60, $"the capture itself changed: {middle}");
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void ATrimmedCornerOnTheClipboardsDibIsWhite_NotBlack()
    {
        using SD.Bitmap shot = Capture(200, 160);
        using SD.Bitmap trimmed = WindowShadowTrim.Trim(shot, radius: 24)!;
        using SD.Bitmap? flattened = AlphaFlatten.OverWhite(trimmed);

        Assert.NotNull(flattened);
        Assert.Equal(SD.Color.FromArgb(255, 255, 255, 255), flattened!.GetPixel(0, 0));
        Assert.Equal(255, flattened.GetPixel(flattened.Width / 2, flattened.Height / 2).A);
    }

    [Fact]
    public void AnOpaqueCaptureIsNotCopiedJustToFlattenIt()
    {
        using var opaque = new SD.Bitmap(20, 20, SD.Imaging.PixelFormat.Format24bppRgb);
        Assert.Null(AlphaFlatten.OverWhite(opaque));
    }

    [Fact]
    public void TheCornerWedgesGoAndNothingElseDoes()
    {
        using SD.Bitmap shot = Capture();
        using SD.Bitmap trimmed = WindowShadowTrim.Trim(shot, radius: 12)!;

        Assert.Equal(shot.Size, trimmed.Size);

        // All four corners, because a rounded rect that only rounds one is a different bug.
        Assert.Equal(0, trimmed.GetPixel(0, 0).A);
        Assert.Equal(0, trimmed.GetPixel(shot.Width - 1, 0).A);
        Assert.Equal(0, trimmed.GetPixel(0, shot.Height - 1).A);
        Assert.Equal(0, trimmed.GetPixel(shot.Width - 1, shot.Height - 1).A);

        // The middle of every edge is the window's own frame and must survive whole.
        Assert.Equal(shot.GetPixel(shot.Width / 2, 0).ToArgb(), trimmed.GetPixel(shot.Width / 2, 0).ToArgb());
        Assert.Equal(shot.GetPixel(0, shot.Height / 2).ToArgb(), trimmed.GetPixel(0, shot.Height / 2).ToArgb());
        Assert.Equal(shot.GetPixel(40, 30).ToArgb(), trimmed.GetPixel(40, 30).ToArgb());

        // Inside the arc, a corner's own pixels are still there.
        Assert.Equal(255, trimmed.GetPixel(12, 12).A);
    }

    /// <summary>
    /// The arc is painted, not clipped, so its edge fades instead of staircasing. Somewhere
    /// along a 12 px corner there has to be a partly-covered pixel; a hard clip has none.
    /// </summary>
    [Fact]
    public void TheCutEdgeIsSoft()
    {
        using SD.Bitmap shot = Capture();
        using SD.Bitmap trimmed = WindowShadowTrim.Trim(shot, radius: 12)!;

        bool partial = false;
        for (int y = 0; y < 12 && !partial; y++)
            for (int x = 0; x < 12 && !partial; x++)
            {
                byte a = trimmed.GetPixel(x, y).A;
                partial = a is > 0 and < 255;
            }

        Assert.True(partial, "the rounded corner should be antialiased, not clipped");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void ASquareCorneredWindowIsLeftAlone(int radius)
    {
        using SD.Bitmap shot = Capture();
        Assert.Null(WindowShadowTrim.Trim(shot, radius));
    }

    /// <summary>A radius bigger than the capture would round away its middle; better to keep
    /// the shot than to hand back a lens.</summary>
    [Fact]
    public void ACaptureTooSmallToRoundKeepsItsCorners()
    {
        using SD.Bitmap tiny = Capture(10, 10);
        Assert.Null(WindowShadowTrim.Trim(tiny, radius: 12));
    }

    [Fact]
    public void TrimNeverTouchesTheCaptureItWasGiven()
    {
        using SD.Bitmap shot = Capture();
        using SD.Bitmap trimmed = WindowShadowTrim.Trim(shot, radius: 12)!;

        Assert.Equal(255, shot.GetPixel(0, 0).A);
        Assert.NotSame(shot, trimmed);
    }

    /// <summary>There is no window behind a null handle, so there is no radius to find and
    /// nothing to trim — the guard the dragged-region path relies on.</summary>
    [Fact]
    public void NoWindowMeansNoTrim()
    {
        using SD.Bitmap shot = Capture();
        Assert.Equal(0, WindowShadowTrim.RadiusFor(IntPtr.Zero));
        Assert.Null(WindowShadowTrim.Trim(shot, IntPtr.Zero));
    }
}
