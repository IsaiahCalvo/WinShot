using System.Drawing.Imaging;
using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The instructional pill on the recording selector. Rendered into a bitmap and pixel-scanned
/// rather than eyeballed: what matters is that it is light-on-dark (the inverse of the size
/// readout), horizontally centred, and completely gone at zero opacity.
/// </summary>
public class SelectorHintPillTests
{
    private const string Hint = "Drag to record part of the screen, or click a window to record it.";
    private static readonly SD.Size Surface = new(1280, 720);

    private static SD.Bitmap Render(string text, double opacity)
    {
        var canvas = new SD.Bitmap(Surface.Width, Surface.Height, PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(canvas))
        {
            g.Clear(SD.Color.FromArgb(255, 20, 20, 20));
            SelectorChrome.DrawHint(g, Surface, text, opacity);
        }
        return canvas;
    }

    private static SD.Rectangle LitBounds(SD.Bitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                // Anything markedly lighter than the 20-grey ground is pill.
                if (bmp.GetPixel(x, y).R <= 90)
                    continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return maxX < 0
            ? SD.Rectangle.Empty
            : SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    [Fact]
    public void Pill_IsLightOnDarkAndHorizontallyCentred()
    {
        using SD.Bitmap bmp = Render(Hint, opacity: 1);

        SD.Rectangle lit = LitBounds(bmp);
        Assert.False(lit.IsEmpty);
        int leftGap = lit.Left;
        int rightGap = Surface.Width - lit.Right;
        Assert.InRange(Math.Abs(leftGap - rightGap), 0, 2);
        // Near the top, not floating in the middle of the selection area.
        Assert.InRange(lit.Top, 1, Surface.Height / 3);
    }

    [Fact]
    public void Pill_HasDarkTextInsideItsLightFill()
    {
        using SD.Bitmap bmp = Render(Hint, opacity: 1);

        SD.Rectangle lit = LitBounds(bmp);
        bool sawDarkInk = false;
        for (int y = lit.Top; y < lit.Bottom && !sawDarkInk; y++)
        {
            for (int x = lit.Left; x < lit.Right; x++)
            {
                if (bmp.GetPixel(x, y).R < 60)
                {
                    sawDarkInk = true;
                    break;
                }
            }
        }

        Assert.True(sawDarkInk, "the hint pill drew no dark text inside its light fill");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Pill_DrawsNothingAtZeroOpacity(double opacity)
    {
        using SD.Bitmap bmp = Render(Hint, opacity);

        Assert.True(LitBounds(bmp).IsEmpty);
    }

    [Fact]
    public void Pill_DrawsNothingWithoutText()
    {
        using SD.Bitmap bmp = Render("", opacity: 1);

        Assert.True(LitBounds(bmp).IsEmpty);
    }

    /// <summary>Half-way through the dismissal fade the pill must be dimmer than at rest —
    /// the fade lives in the alpha, not in a visibility flag.</summary>
    [Fact]
    public void Pill_IsDimmerMidFade()
    {
        using SD.Bitmap full = Render(Hint, opacity: 1);
        using SD.Bitmap half = Render(Hint, opacity: 0.5);

        SD.Rectangle lit = LitBounds(full);
        SD.Point sample = new(lit.Left + 3, lit.Top + (lit.Height / 2));
        Assert.True(
            half.GetPixel(sample.X, sample.Y).R < full.GetPixel(sample.X, sample.Y).R,
            "the fading pill was not dimmer than the resting one");
    }
}
