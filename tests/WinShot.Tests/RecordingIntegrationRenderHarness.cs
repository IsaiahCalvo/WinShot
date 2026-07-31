using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class RecordingIntegrationRenderHarness
{
    [Fact]
    public void RenderSanitizedRecordingState()
    {
        string? outputDir = Environment.GetEnvironmentVariable("WINSHOT_RECORDING_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(outputDir))
            return;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(outputDir);
                using var canvas = new SD.Bitmap(1280, 720, PixelFormat.Format32bppArgb);
                using SD.Graphics graphics = SD.Graphics.FromImage(canvas);
                DrawSanitizedDesktop(graphics, canvas.Size);

                var recording = new SD.Rectangle(180, 105, 900, 500);
                using (var dim = new SD.SolidBrush(SD.Color.FromArgb(115, 0, 0, 0)))
                {
                    foreach (SD.Rectangle rect in RecordingDimOverlayLayout.OutsideRecordingArea(
                        new SD.Rectangle(SD.Point.Empty, canvas.Size), recording))
                    {
                        graphics.FillRectangle(dim, rect);
                    }
                }

                using (var border = new SD.Pen(SD.Color.FromArgb(90, 255, 255, 255), 1))
                    graphics.DrawRectangle(border, recording);

                using var bar = new FastRecordingControlBar(recording, showTimer: true)
                {
                    Location = new SD.Point(-6000, -6000),
                };
                bar.Show();
                bar.PerformLayout();
                using var barImage = new SD.Bitmap(bar.Width, bar.Height, PixelFormat.Format32bppArgb);
                bar.DrawToBitmap(barImage, new SD.Rectangle(SD.Point.Empty, barImage.Size));
                bar.Close();

                int barX = (canvas.Width - barImage.Width) / 2;
                int barY = canvas.Height - barImage.Height - 24;
                graphics.DrawImageUnscaled(barImage, barX, barY);

                canvas.Save(Path.Combine(outputDir, "recording-state-dim-controls-timer.png"), ImageFormat.Png);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(File.Exists(Path.Combine(outputDir, "recording-state-dim-controls-timer.png")));
    }

    private static void DrawSanitizedDesktop(SD.Graphics graphics, SD.Size size)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(SD.Color.FromArgb(236, 239, 244));
        using var header = new SD.SolidBrush(SD.Color.FromArgb(42, 48, 61));
        graphics.FillRectangle(header, 0, 0, size.Width, 74);
        using var titleFont = new SD.Font("Segoe UI", 18, SD.FontStyle.Bold);
        using var bodyFont = new SD.Font("Segoe UI", 12);
        using var white = new SD.SolidBrush(SD.Color.White);
        using var dark = new SD.SolidBrush(SD.Color.FromArgb(55, 60, 70));
        graphics.DrawString("Sanitized recording preview", titleFont, white, 30, 22);
        for (int i = 0; i < 3; i++)
        {
            var card = new SD.Rectangle(80 + i * 395, 150, 340, 330);
            using var cardBrush = new SD.SolidBrush(SD.Color.White);
            graphics.FillRoundedRectangle(cardBrush, card, 16);
            graphics.DrawString($"Sample panel {i + 1}", bodyFont, dark, card.X + 22, card.Y + 24);
        }
    }
}

internal static class RecordingRenderGraphicsExtensions
{
    public static void FillRoundedRectangle(
        this SD.Graphics graphics,
        SD.Brush brush,
        SD.Rectangle bounds,
        int radius)
    {
        using var path = new GraphicsPath();
        int diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
