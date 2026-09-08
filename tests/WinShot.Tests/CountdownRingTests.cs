using System.Drawing.Imaging;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The countdown badge shared by the recording countdown and the capture self-timer. The
/// arithmetic is pure, so the sweep and the digit's pop are pinned here rather than watched.
/// </summary>
public class CountdownRingTests
{
    [Fact]
    public void Sweep_IsAFullCircleAtTheStartAndNothingAtTheEnd()
    {
        Assert.Equal(360f, CountdownRing.SweepDegrees(0));
        Assert.Equal(0f, CountdownRing.SweepDegrees(1));
        Assert.Equal(180f, CountdownRing.SweepDegrees(0.5), 3);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void Sweep_ClampsOutOfRangeProgress(double progress) =>
        Assert.InRange(CountdownRing.SweepDegrees(progress), 0f, 360f);

    [Fact]
    public void Progress_CountsWholeSecondsPlusTheFractionInsideTheCurrentOne()
    {
        Assert.Equal(0, CountdownRing.Progress(totalSeconds: 4, remainingSeconds: 4, secondFraction: 0), 6);
        Assert.Equal(0.25, CountdownRing.Progress(4, 4, 1), 6);
        Assert.Equal(0.5, CountdownRing.Progress(4, 3, 1), 6);
        Assert.Equal(1, CountdownRing.Progress(4, 1, 1), 6);
    }

    [Fact]
    public void Progress_IsCompleteForADegenerateCountdown() =>
        Assert.Equal(1, CountdownRing.Progress(totalSeconds: 0, remainingSeconds: 0, secondFraction: 0));

    /// <summary>The digit lands from oversized onto its real size early in the second and then
    /// holds — a pop that ran the whole second would read as a wobble.</summary>
    [Fact]
    public void DigitScale_LandsEarlyAndThenHolds()
    {
        Assert.Equal(CountdownRing.DigitPopScale, CountdownRing.DigitScale(0), 6);
        Assert.True(CountdownRing.DigitScale(0.05) < CountdownRing.DigitPopScale);
        Assert.True(CountdownRing.DigitScale(0.05) > 1);
        Assert.Equal(1, CountdownRing.DigitScale(CountdownRing.DigitPopFraction), 6);
        Assert.Equal(1, CountdownRing.DigitScale(0.9), 6);
        Assert.Equal(1, CountdownRing.DigitScale(1), 6);
    }

    /// <summary>With animation off system-wide the per-second tween completes immediately, so
    /// every frame is drawn at fraction 1 — the countdown must still look right there.</summary>
    [Fact]
    public void WithoutAnimation_TheBadgeStillStepsOncePerSecond()
    {
        Assert.Equal(1, CountdownRing.DigitScale(1), 6);
        Assert.Equal(0.5, CountdownRing.Progress(totalSeconds: 4, remainingSeconds: 3, secondFraction: 1), 6);
    }

    [Fact]
    public void Draw_PaintsAnArcThatShrinksAsTheCountdownRuns()
    {
        int Lit(double progress)
        {
            using var canvas = new SD.Bitmap(170, 170, PixelFormat.Format32bppArgb);
            using (SD.Graphics g = SD.Graphics.FromImage(canvas))
            {
                g.Clear(SD.Color.Black);
                using var font = new SD.Font("Segoe UI", 40, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
                CountdownRing.Draw(
                    g,
                    canvas.Size,
                    ringThickness: 4,
                    progress,
                    digits: "",           // arc only: the digit would swamp the pixel count
                    font,
                    SD.Color.White,
                    SD.Color.FromArgb(0, 0, 0, 0), // invisible track, so only the arc counts
                    ThemePalette.Accent,
                    new SD.Rectangle(0, 0, 170, 120),
                    digitScale: 1);
            }

            int lit = 0;
            for (int y = 0; y < canvas.Height; y++)
            {
                for (int x = 0; x < canvas.Width; x++)
                {
                    if (canvas.GetPixel(x, y).B > 40)
                        lit++;
                }
            }
            return lit;
        }

        int full = Lit(0);
        int half = Lit(0.5);
        int done = Lit(1);

        Assert.True(full > 0, "no arc drawn at the start of the countdown");
        Assert.True(half < full, $"half-way arc ({half}) was not shorter than the full one ({full})");
        Assert.True(done < half);
    }
}
