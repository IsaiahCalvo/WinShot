using System.Drawing.Imaging;
using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The instructional pill on the recording selector. It is the app's ONE hint pill —
/// <see cref="SelectorChrome.DrawHint"/>, the same surface the first-run hint and the message
/// pill ride — so these assertions describe that pill's shape and fade, not a second
/// recording-only style. Rendered into a bitmap and pixel-scanned rather than eyeballed.
/// </summary>
public class SelectorHintPillTests
{
    private const string Hint = "Drag to record part of the screen, or click a window to record it.";
    private static readonly SD.Size Surface = new(1280, 720);

    /// <summary>Mid-grey: far enough from both the pill's dark fill and its light text that
    /// either one counts as "pill" against it.</summary>
    private static readonly SD.Color Ground = SD.Color.FromArgb(255, 128, 128, 128);

    private static SD.Bitmap Render(string text, double opacity) => Render(text, opacity, Ground);

    private static SD.Bitmap Render(string text, double opacity, SD.Color ground)
    {
        var canvas = new SD.Bitmap(Surface.Width, Surface.Height, PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(canvas))
        {
            g.Clear(ground);
            SelectorChrome.DrawHint(g, Surface, text, opacity, scale: 1d);
        }
        return canvas;
    }

    /// <summary>Everything that differs from the ground — the capsule and its text together.</summary>
    private static SD.Rectangle PillBounds(SD.Bitmap bmp, SD.Color ground)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                SD.Color p = bmp.GetPixel(x, y);
                if (Math.Abs(p.R - ground.R) <= 8 && Math.Abs(p.G - ground.G) <= 8 &&
                    Math.Abs(p.B - ground.B) <= 8)
                {
                    continue;
                }
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

    private static SD.Rectangle PillBounds(SD.Bitmap bmp) => PillBounds(bmp, Ground);

    [Fact]
    public void Pill_IsHorizontallyCentredNearTheTop()
    {
        using SD.Bitmap bmp = Render(Hint, opacity: 1);

        SD.Rectangle pill = PillBounds(bmp);
        Assert.False(pill.IsEmpty);
        int leftGap = pill.Left;
        int rightGap = Surface.Width - pill.Right;
        Assert.InRange(Math.Abs(leftGap - rightGap), 0, 2);
        // Near the top, not floating in the middle of the selection area.
        Assert.InRange(pill.Top, 1, Surface.Height / 3);
    }

    /// <summary>Light text on the app's dark pill fill — the same contrast pairing every other
    /// selector pill uses, so the hint cannot drift into a palette of its own.</summary>
    [Fact]
    public void Pill_HasLightTextInsideItsDarkFill()
    {
        using SD.Bitmap bmp = Render(Hint, opacity: 1);

        SD.Rectangle pill = PillBounds(bmp);
        int dark = 0, light = 0;
        for (int y = pill.Top; y < pill.Bottom; y++)
        {
            for (int x = pill.Left; x < pill.Right; x++)
            {
                int r = bmp.GetPixel(x, y).R;
                if (r < 70) dark++;
                else if (r > 190) light++;
            }
        }

        Assert.True(dark > 200, $"the hint pill drew no dark fill ({dark} px)");
        Assert.True(light > 50, $"the hint pill drew no light text ({light} px)");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Pill_DrawsNothingAtZeroOpacity(double opacity)
    {
        using SD.Bitmap bmp = Render(Hint, opacity);

        Assert.True(PillBounds(bmp).IsEmpty);
    }

    [Fact]
    public void Pill_DrawsNothingWithoutText()
    {
        using SD.Bitmap bmp = Render("", opacity: 1);

        Assert.True(PillBounds(bmp).IsEmpty);
    }

    /// <summary>
    /// Half-way through the dismissal fade the pill must be closer to the ground than at rest —
    /// the fade lives in the alpha, not in a visibility flag. Sampled just inside the capsule,
    /// clear of the text, where a fading dark fill moves up toward the mid-grey ground.
    /// </summary>
    [Fact]
    public void Pill_IsDimmerMidFade()
    {
        using SD.Bitmap full = Render(Hint, opacity: 1);
        using SD.Bitmap half = Render(Hint, opacity: 0.5);

        SD.Rectangle pill = PillBounds(full);
        SD.Point sample = new(pill.Left + 3, pill.Top + (pill.Height / 2));
        Assert.True(
            half.GetPixel(sample.X, sample.Y).R > full.GetPixel(sample.X, sample.Y).R,
            "the fading pill was not closer to the ground than the resting one");
    }

    /// <summary>
    /// The sentence has to fade WITH its capsule. GDI's TextRenderer drops the alpha channel,
    /// so an alpha-scaled ink colour would fade only the fill underneath: the capsule dissolves
    /// out from under solid text that then vanishes in a single frame. At one eighth the text
    /// must be nearly gone, and mid-fade it must still be there, still lighter than the fill.
    /// </summary>
    [Fact]
    public void Pill_InkFadesWithItsCapsule()
    {
        SD.Rectangle box;
        using (SD.Bitmap rest = Render(Hint, opacity: 1))
            box = PillBounds(rest);

        static int LightestInk(SD.Bitmap bmp, SD.Rectangle box)
        {
            int lightest = 0;
            for (int y = box.Top; y < box.Bottom; y++)
            {
                for (int x = box.Left; x < box.Right; x++)
                    lightest = Math.Max(lightest, bmp.GetPixel(x, y).R);
            }
            return lightest;
        }

        using SD.Bitmap full = Render(Hint, opacity: 1);
        using SD.Bitmap eighth = Render(Hint, opacity: 0.125);

        int solid = LightestInk(full, box);
        int fading = LightestInk(eighth, box);
        Assert.InRange(solid, 190, 255);
        Assert.True(
            fading < solid - 40,
            $"the ink did not fade with the capsule: {solid} at rest, {fading} at one-eighth");

        // ...and it fades rather than disappearing. The pill is composited through an ARGB
        // buffer; text that lost its alpha on the way into that buffer would satisfy the
        // assertion above by not being drawn at all, so mid-fade the ink must still be there.
        using SD.Bitmap mid = Render(Hint, opacity: 0.6);
        SD.Rectangle interior = SD.Rectangle.Inflate(box, -box.Height, 0);
        int ink = 0;
        for (int y = interior.Top; y < interior.Bottom; y++)
        {
            for (int x = interior.Left; x < interior.Right; x++)
            {
                if (mid.GetPixel(x, y).R > 150)
                    ink++;
            }
        }

        Assert.True(ink > 20, $"the pill lost its text on the way through the fade ({ink} ink px)");
    }
}
