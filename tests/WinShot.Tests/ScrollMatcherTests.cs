using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Exercises the robust (two-tier) scroll matcher: exact hashing for pixel-perfect
/// content, gradient-profile correlation for frames perturbed by sub-pixel AA noise
/// (the browser/ClearType case that broke the old exact-only matcher), the gates that
/// refuse to guess on whitespace/repetitive content, and canvas re-locking (scroll back
/// up to recover a too-fast scroll).
/// </summary>
public class ScrollMatcherTests
{
    private const int Width = 320;
    private const int Height = 400;

    /// <summary>
    /// Deterministic pseudo-random per-row color. Consecutive document rows differ by tens
    /// of luma units — like text edges — so the gradient-energy profile carries a strong,
    /// aperiodic signal. (A linear row→color encoding produces a near-periodic sawtooth
    /// with tiny inter-row deltas: pathological content no real page resembles.)
    /// </summary>
    private static SD.Color RowColor(int globalRow)
    {
        int h = Hash(globalRow, 0, 99);
        return SD.Color.FromArgb(255, h & 0xFF, (h >> 8) & 0xFF, (h >> 16) & 0xFF);
    }

    /// <summary>
    /// Builds a viewport frame showing document rows [top, top+height), optionally adding
    /// deterministic per-pixel noise of amplitude ±<paramref name="noise"/> — simulating the
    /// sub-pixel re-rasterization that makes real scrolled frames not byte-identical.
    /// The noise depends on (x, viewport-y, seed), NOT the document row, exactly like AA:
    /// the same content produces different pixels at different scroll positions.
    /// </summary>
    private static SD.Bitmap MakeFrame(int top, int noise = 0, int seed = 0, int height = Height)
    {
        var bmp = new SD.Bitmap(Width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, Width, height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Width * 4];
            for (int y = 0; y < height; y++)
            {
                var c = RowColor(top + y);
                for (int x = 0; x < Width; x++)
                {
                    int i = x * 4;
                    int n = noise == 0 ? 0 : Hash(x, y, seed) % (2 * noise + 1) - noise;
                    row[i] = ClampByte(c.B + n);
                    row[i + 1] = ClampByte(c.G + n);
                    row[i + 2] = ClampByte(c.R + n);
                    row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    private static int Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (int)((h ^ (h >> 16)) & 0x7FFFFFFF);
        }
    }

    private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);

    private static FrameSignature Sig(SD.Bitmap bmp) => FrameSignature.Build(bmp);

    // ------------------------------------------------------------ two-tier offset

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(150)]
    public void FindOffset_ExactFrames_DetectsScrollAmount(int k)
    {
        using var f1 = MakeFrame(0);
        using var f2 = MakeFrame(k);

        Assert.Equal(k, ScrollMatcher.FindOffset(Sig(f1), Sig(f2)));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(90)]
    public void FindOffset_NoisyFrames_StillDetectsScrollAmount(int k)
    {
        // ±6 per-pixel noise differing between the frames: no row is byte-identical, so
        // the exact tier finds nothing and the correlation tier must carry it.
        using var f1 = MakeFrame(0, noise: 6, seed: 1);
        using var f2 = MakeFrame(k, noise: 6, seed: 2);

        Assert.Equal(k, ScrollMatcher.FindOffset(Sig(f1), Sig(f2)));
    }

    [Fact]
    public void FindOffset_WhitespaceFrames_RefusesToGuess()
    {
        using var f1 = MakeSolid(SD.Color.FromArgb(255, 250, 250, 250), noiseSeed: 1);
        using var f2 = MakeSolid(SD.Color.FromArgb(255, 250, 250, 250), noiseSeed: 2);

        Assert.Equal(0, ScrollMatcher.FindOffset(Sig(f1), Sig(f2)));
    }

    [Fact]
    public void FindOffset_RepetitiveContent_RefusesToGuess()
    {
        // Rows repeat with period 16 (think table gridlines): the true offset is
        // indistinguishable from offset±16, so the unique-peak gate must reject.
        using var f1 = MakePeriodic(period: 16, phase: 0, noise: 4, seed: 1);
        using var f2 = MakePeriodic(period: 16, phase: 5, noise: 4, seed: 2);

        Assert.Equal(0, ScrollMatcher.FindOffset(Sig(f1), Sig(f2)));
    }

    [Fact]
    public void FindOffset_UnrelatedFrames_ReturnsZero()
    {
        using var f1 = MakeFrame(0, noise: 6, seed: 1);
        using var f2 = MakeFrame(10_000, noise: 6, seed: 2);

        Assert.Equal(0, ScrollMatcher.FindOffset(Sig(f1), Sig(f2)));
    }

    [Fact]
    public void FindOffset_WithStickyFooterBand_DetectsBodyOffsetUnderNoise()
    {
        const int footer = 40, k = 60;
        using var f1 = MakeFooterFrame(0, footer, noise: 6, seed: 1);
        using var f2 = MakeFooterFrame(k, footer, noise: 6, seed: 2);

        Assert.Equal(k, ScrollMatcher.FindOffset(Sig(f1), Sig(f2), footer));
    }

    // ------------------------------------------------------------ identity / information

    [Fact]
    public void Identical_TrueOnlyForSamePixels()
    {
        using var a = MakeFrame(0);
        using var b = MakeFrame(0);
        using var c = MakeFrame(5);

        Assert.True(ScrollMatcher.Identical(Sig(a), Sig(b)));
        Assert.False(ScrollMatcher.Identical(Sig(a), Sig(c)));
    }

    [Fact]
    public void IsLowInformation_TrueForBlank_FalseForContent()
    {
        using var blank = MakeSolid(SD.Color.White, noiseSeed: 0);
        using var content = MakeFrame(0);

        Assert.True(ScrollMatcher.IsLowInformation(Sig(blank)));
        Assert.False(ScrollMatcher.IsLowInformation(Sig(content)));
    }

    // ------------------------------------------------------------ canvas re-lock

    [Fact]
    public void LocateInCanvas_FrameOverhangingEnd_ReturnsOverhang()
    {
        // Canvas = document rows 0..1000. Live frame shows rows 700..1100 → 100 new rows.
        using var canvasFrame = MakeFrame(0, height: 1000);
        var canvas = new CanvasProfile();
        var canvasSig = Sig(canvasFrame);
        canvas.Append(canvasSig, 0, 1000);

        using var frame = MakeFrame(700, noise: 6, seed: 3);
        var l = ScrollMatcher.LocateInCanvas(canvas, Sig(frame));

        Assert.NotNull(l);
        Assert.Equal(700, l!.Value.Position);
        Assert.Equal(100, l.Value.NewRows);
    }

    [Fact]
    public void LocateInCanvas_FrameFullyInside_ReturnsZeroNewRows()
    {
        using var canvasFrame = MakeFrame(0, height: 1000);
        var canvas = new CanvasProfile();
        canvas.Append(Sig(canvasFrame), 0, 1000);

        using var frame = MakeFrame(300, noise: 6, seed: 3); // rows 300..700, all captured
        var l = ScrollMatcher.LocateInCanvas(canvas, Sig(frame));

        Assert.NotNull(l);
        Assert.Equal(300, l!.Value.Position);
        Assert.Equal(0, l.Value.NewRows);
    }

    [Fact]
    public void LocateInCanvas_UnrelatedFrame_ReturnsNull()
    {
        using var canvasFrame = MakeFrame(0, height: 1000);
        var canvas = new CanvasProfile();
        canvas.Append(Sig(canvasFrame), 0, 1000);

        using var frame = MakeFrame(50_000, noise: 6, seed: 3);
        Assert.Null(ScrollMatcher.LocateInCanvas(canvas, Sig(frame)));
    }

    /// <summary>
    /// The scenario the rewrite exists for: the user flicks past the overlap (gap), the
    /// matcher refuses to stitch, then the user scrolls BACK UP and slowly down again —
    /// the canvas locate must recover the alignment and report exactly the missing span.
    /// </summary>
    [Fact]
    public void LocateInCanvas_ScrollBackAfterGap_RecoversAlignment()
    {
        using var canvasFrame = MakeFrame(0, height: 800); // captured up to row 800
        var canvas = new CanvasProfile();
        canvas.Append(Sig(canvasFrame), 0, 800);

        // Flick: frame at rows 1300..1700 — zero overlap with the canvas.
        using var flick = MakeFrame(1300, noise: 6, seed: 4);
        Assert.Null(ScrollMatcher.LocateInCanvas(canvas, Sig(flick)));

        // User scrolls back up: frame at rows 600..1000 overlaps the canvas tail by 200 rows.
        using var back = MakeFrame(600, noise: 6, seed: 5);
        var l = ScrollMatcher.LocateInCanvas(canvas, Sig(back));

        Assert.NotNull(l);
        Assert.Equal(600, l!.Value.Position);
        Assert.Equal(200, l.Value.NewRows); // exactly the rows missing past the old end
    }

    // ------------------------------------------------------------ builders

    private static SD.Bitmap MakeSolid(SD.Color color, int noiseSeed)
    {
        var bmp = new SD.Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }

    private static SD.Bitmap MakePeriodic(int period, int phase, int noise, int seed)
    {
        var bmp = new SD.Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, Width, Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Width * 4];
            for (int y = 0; y < Height; y++)
            {
                var c = RowColor((y + phase) % period);
                for (int x = 0; x < Width; x++)
                {
                    int i = x * 4;
                    int n = Hash(x, y, seed) % (2 * noise + 1) - noise;
                    row[i] = ClampByte(c.B + n);
                    row[i + 1] = ClampByte(c.G + n);
                    row[i + 2] = ClampByte(c.R + n);
                    row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>Frame with a scrolling body over document rows [top, ...) and a constant
    /// (byte-identical across frames, noise-free) sticky footer pinned at the bottom.</summary>
    private static SD.Bitmap MakeFooterFrame(int top, int footer, int noise, int seed)
    {
        var bmp = MakeFrame(top, noise, seed);
        var data = bmp.LockBits(new SD.Rectangle(0, Height - footer, Width, footer),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Width * 4];
            for (int y = 0; y < footer; y++)
            {
                var c = RowColor(2_000_000 + y); // body-disjoint constant band
                for (int x = 0; x < Width; x++)
                {
                    int i = x * 4;
                    row[i] = c.B; row[i + 1] = c.G; row[i + 2] = c.R; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
