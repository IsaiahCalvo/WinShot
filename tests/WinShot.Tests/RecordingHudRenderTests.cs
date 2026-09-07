using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Pixel coverage for the recording HUD — the largest surface this track added, and the one
/// piece of it that is hand-drawn rather than laid out by a pure helper. RecordingHudTests pins
/// the geometry; these paint it for real at 100% and 150% and scan the result, so a card that
/// stops filling, a toggle that stops reading as on, a hairline that drifts off the boundary
/// RecordingHudLayout reports, or an "Esc to cancel" that goes back to bare text on the desktop
/// all fail here rather than needing an eye on a screenshot.
///
/// The ground is deliberately LIGHT: what sits behind the HUD is the dimmed desktop, so a white
/// window is the failing case for anything the HUD does not paint a container for.
/// </summary>
[Collection("MotionRefcount")]
public class RecordingHudRenderTests
{
    /// <summary>The desktop as it composites through the selector's 115-alpha dim: a white
    /// window survives at #8C8C8C, which is what made bare TextSecondary 1.81:1 there.</summary>
    private static readonly SD.Color DimmedWhiteDesktop = SD.Color.FromArgb(0x8C, 0x8C, 0x8C);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void EveryCellSitsOnTheCardsOwnFillRatherThanOnTheDesktop(double scale)
    {
        using Painted painted = PaintHud(scale, AllOff);

        foreach ((RecordingHudCell cell, SD.Rectangle bounds) in painted.Hud.Cells)
        {
            // The cell corners, not its centre: the centre is where the label sits, and
            // ClearType puts colour fringes on it that say nothing about what is underneath.
            SD.Point[] corners =
            [
                new(bounds.Left + 4, bounds.Top + 3),
                new(bounds.Right - 4, bounds.Bottom - 3),
            ];
            foreach (SD.Point at in corners)
            {
                SD.Color sample = painted.At(at);
                Assert.True(
                    Luminance(sample) < 0.30,
                    $"{cell} at {scale:0.0}x painted on a bare desktop, not on a card: {sample}");
            }
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void AToggleReadsMeasurablyDifferentOnAndOff(double scale)
    {
        using Painted dark = PaintHud(scale, AllOff);
        using Painted lit = PaintHud(scale, AllOff.Toggled(RecordingHudCell.Microphone));

        SD.Rectangle cell = dark.Hud.CellBounds(RecordingHudCell.Microphone);
        Assert.False(cell.IsEmpty);

        double difference = MeanChannelDifference(dark, lit, cell);
        Assert.True(difference > 4, $"the microphone plate barely changed when lit: {difference:0.00}");

        // Every other toggle has to be untouched, or the plate is not showing that one cell's state.
        Assert.True(
            MeanChannelDifference(dark, lit, dark.Hud.CellBounds(RecordingHudCell.Webcam)) < 1,
            "lighting the microphone changed the webcam cell too");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void TheHairlinesLandOnTheCellBoundariesTheLayoutReports(double scale)
    {
        using Painted painted = PaintHud(scale, AllOff);

        SD.Rectangle settings = painted.Hud.SettingsCard;
        int y = settings.Top + (settings.Height / 2);

        for (int i = 1; i < painted.Hud.Cells.Count; i++)
        {
            (RecordingHudCell cell, SD.Rectangle bounds) = painted.Hud.Cells[i];
            if (cell is RecordingHudCell.RecordGif or RecordingHudCell.RecordVideo)
                continue;

            double onTheLine = Luminance(painted.At(new SD.Point(bounds.Left, y)));
            double beside = Luminance(painted.At(new SD.Point(bounds.Left + (int)Math.Round(6 * scale), y)));
            Assert.True(
                onTheLine > beside + 0.004,
                $"no hairline at the {cell} boundary ({bounds.Left}): {onTheLine:0.0000} vs {beside:0.0000}");
        }
    }

    /// <summary>
    /// The regression test for the HUD's one uncontained string. "Esc to cancel" used to sit
    /// straight on the dimmed desktop in TextSecondary — 1.81:1 over a white window, under even
    /// the 3:1 non-text floor. It has to ride the same pill every other floating label here gets.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void TheEscapeHintIsOnAPillRatherThanBareOnTheDesktop(double scale)
    {
        using Painted painted = PaintHud(scale, AllOff);

        // PaintHud puts the hint under the block, flipping above it when the block is already
        // parked on the monitor's bottom edge. Either band is fine; a bare string in neither is not.
        int below = painted.Hud.ActionCard.Bottom + (int)Math.Round(10 * scale);
        int above = painted.Hud.SettingsCard.Top - (int)Math.Round(10 * scale);
        int centreX = painted.Hud.ActionCard.Left + (painted.Hud.ActionCard.Width / 2);
        int reach = (int)Math.Round(20 * scale);

        int best = Math.Max(DarkPixels(painted, centreX, below, reach), DarkPixels(painted, centreX, above, reach));
        Assert.True(best > reach, $"the Esc hint is still bare on the desktop at {scale:0.0}x ({best} dark px)");
    }

    /// <summary>Writes the scanned frames out when the harness asks for them, so the pixels a
    /// reviewer looks at are the ones the assertions above ran on.</summary>
    [Fact]
    public void RenderHudFrames()
    {
        string? outputDir = Environment.GetEnvironmentVariable("WINSHOT_RECORDING_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(outputDir))
            return;

        Directory.CreateDirectory(outputDir);
        foreach (double scale in new[] { 1.0, 1.5 })
        {
            using Painted painted = PaintHud(scale, AllOff.Toggled(RecordingHudCell.Microphone));
            string path = Path.Combine(outputDir, $"recording-hud-{scale * 100:0}.png");
            painted.Canvas.Save(path, ImageFormat.Png);
            Assert.True(File.Exists(path));
        }
    }

    // ---- harness ----------------------------------------------------------------------

    private static readonly RecordingHudToggles AllOff = new(false, false, false, false, false);

    private sealed class Painted(SD.Bitmap canvas, SD.Rectangle bounds, RecordingHudGeometry hud) : IDisposable
    {
        public SD.Bitmap Canvas { get; } = canvas;
        public RecordingHudGeometry Hud { get; } = hud;

        /// <summary>A screen point on the painted canvas, clamped to it.</summary>
        public SD.Color At(SD.Point screen) => Canvas.GetPixel(
            Math.Clamp(screen.X - bounds.X, 0, Canvas.Width - 1),
            Math.Clamp(screen.Y - bounds.Y, 0, Canvas.Height - 1));

        public void Dispose() => Canvas.Dispose();
    }

    /// <summary>
    /// Paints the HUD for <paramref name="scale"/> onto a light ground, through the selector's
    /// own PaintHud so the test can never diverge from what the app draws. Nothing is shown, the
    /// real mouse is never touched, and no real DPI is consulted — RecordingMonitorDpi.ScaleSource
    /// pins the scale so 150% is scannable without a 150% monitor to draw on.
    /// </summary>
    private static Painted PaintHud(double scale, RecordingHudToggles toggles)
    {
        Painted? result = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            RecordingMonitorDpi.ScaleSource = _ => scale;
            Func<bool> previousAnimations = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false; // nothing may be mid-tween while we scan
            try
            {
                // Anchored to a real monitor: PaintHud asks Screen.FromRectangle where the
                // region lives, so a made-up monitor rect would place the cards somewhere the
                // assertions are not looking.
                SD.Rectangle monitor = WF.Screen.PrimaryScreen!.Bounds;
                var region = new SD.Rectangle(
                    monitor.Left + (monitor.Width / 4),
                    monitor.Top + (monitor.Height / 8),
                    Math.Max(200, monitor.Width / 3),
                    Math.Max(120, monitor.Height / 4));

                using var selector = new FastRegionSelectorDialog(
                    () => Task.FromResult(new List<WindowInfo>()),
                    settings: null);
                SetField(selector, "_vs", monitor);
                SetField(selector, "_monitorBounds", monitor);
                SetField(selector, "_recordingHud", true);
                SetField(selector, "_recordingToggles", toggles);
                SetField(selector, "_pendingScreen", (SD.Rectangle?)region);
                Invoke(selector, "ResetHudMotion");

                RecordingHudGeometry hud = RecordingHudLayout.Build(region, monitor, scale);
                SD.Rectangle bounds = SD.Rectangle.Inflate(hud.Bounds, 72, 72);

                var canvas = new SD.Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (SD.Graphics g = SD.Graphics.FromImage(canvas))
                {
                    g.Clear(DimmedWhiteDesktop);
                    Invoke(selector, "PaintHud", g, bounds, region);
                }
                result = new Painted(canvas, bounds, hud);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Motion.AnimationsEnabled = previousAnimations;
                RecordingMonitorDpi.ScaleSource = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.NotNull(result);
        return result!;
    }

    private static int DarkPixels(Painted painted, int centreX, int y, int reach)
    {
        int count = 0;
        for (int x = centreX - reach; x <= centreX + reach; x++)
        {
            if (Luminance(painted.At(new SD.Point(x, y))) < 0.30)
                count++;
        }
        return count;
    }

    private static double MeanChannelDifference(Painted a, Painted b, SD.Rectangle screenRect)
    {
        double total = 0;
        int samples = 0;
        for (int y = screenRect.Top + 2; y < screenRect.Bottom - 2; y += 2)
        {
            for (int x = screenRect.Left + 2; x < screenRect.Right - 2; x += 2)
            {
                var at = new SD.Point(x, y);
                SD.Color first = a.At(at);
                SD.Color second = b.At(at);
                total += (Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) +
                          Math.Abs(first.B - second.B)) / 3d;
                samples++;
            }
        }
        return samples == 0 ? 0 : total / samples;
    }

    private static double Luminance(SD.Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(int value)
    {
        double v = value / 255d;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static void SetField(object target, string name, object? value) =>
        typeof(FastRegionSelectorDialog)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static void Invoke(object target, string name, params object?[] args) =>
        typeof(FastRegionSelectorDialog)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);
}
