using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using WinShot.Core;
using WinShot.Pin;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Gated, in-process visual evidence for a pinned screenshot. The source image is
/// synthetic and the installed WinShot application is never launched or controlled.
/// </summary>
public class PinRenderHarness
{
    [Fact]
    public void RenderPinnedScreenshotToPng()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_PIN") != "1")
            return;

        string output = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-pin-render.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = CreateSyntheticCapture();
                var settings = new SettingsService();
                settings.Current.PinnedRoundedCorners = true;
                settings.Current.PinnedShadow = true;
                settings.Current.PinnedBorder = true;

                using var pin = new FastPinWindow(source, settings);
                pin.CreateControl();
                SetField(pin, "_mouseInside", true);
                SetField(pin, "_hoverButton", 1);
                SetField(pin, "_focusButton", 1);
                Render(pin, output, showKeyboardFocus: true);
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
        Assert.True(File.Exists(output));
    }

    private static void Render(FastPinWindow pin, string path, bool showKeyboardFocus)
    {
        using var bitmap = new SD.Bitmap(pin.ClientSize.Width, pin.ClientSize.Height);
        using var graphics = SD.Graphics.FromImage(bitmap);
        graphics.Clear(SD.Color.Transparent);
        if (pin.Region is not null)
            graphics.SetClip(pin.Region, CombineMode.Intersect);

        using var args = new WF.PaintEventArgs(graphics, new SD.Rectangle(SD.Point.Empty, bitmap.Size));
        typeof(FastPinWindow)
            .GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, new object[] { args });
        if (showKeyboardFocus)
        {
            var type = typeof(FastPinWindow);
            var bounds = (SD.Rectangle)type
                .GetMethod("ButtonBounds", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pin, new object[] { 1 })!;
            string glyph = (string)type
                .GetMethod("GlyphFor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pin, new object[] { 1 })!;
            type.GetMethod("DrawButton", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { graphics, bounds, glyph, true, true });
        }
        bitmap.Save(path, SD.Imaging.ImageFormat.Png);
    }

    private static void SetField(FastPinWindow pin, string name, object value)
        => typeof(FastPinWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pin, value);

    private static SD.Bitmap CreateSyntheticCapture()
    {
        var bitmap = new SD.Bitmap(900, 560);
        using var graphics = SD.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var gradient = new LinearGradientBrush(
            new SD.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            SD.Color.FromArgb(19, 47, 79),
            SD.Color.FromArgb(32, 128, 120),
            22f);
        graphics.FillRectangle(gradient, 0, 0, bitmap.Width, bitmap.Height);

        using var card = new SD.SolidBrush(SD.Color.FromArgb(232, 246, 249, 252));
        using var cardPath = GdiPaths.RoundedRect(new SD.Rectangle(80, 70, 740, 420), 34);
        graphics.FillPath(card, cardPath);
        using var accent = new SD.SolidBrush(SD.Color.FromArgb(40, 126, 220));
        graphics.FillEllipse(accent, 135, 135, 115, 115);
        using var titleFont = new SD.Font("Segoe UI", 34, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
        using var bodyFont = new SD.Font("Segoe UI", 22, SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
        graphics.DrawString("SANITIZED PIN", titleFont, SD.Brushes.Black, 300, 150);
        graphics.DrawString("Synthetic render evidence", bodyFont, SD.Brushes.DimGray, 300, 215);
        using var line = new SD.Pen(SD.Color.FromArgb(98, 115, 134), 14)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(line, 140, 340, 720, 340);
        graphics.DrawLine(line, 140, 395, 600, 395);
        return bitmap;
    }
}
