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
    public static IEnumerable<object[]> RoundedCornerScenarios()
    {
        yield return new object[] { "landscape-100-border-locked", 320, 180, 96, true, false, true, false };
        yield return new object[] { "wide-125-hover-shadow", 701, 141, 120, false, true, false, true };
        yield return new object[] { "portrait-150-border-shadow", 141, 701, 144, true, true, false, false };
        yield return new object[] { "small-200-hover", 47, 31, 192, false, false, false, true };
        yield return new object[] { "square-100-border-hover", 211, 211, 96, true, true, false, true };
        yield return new object[] { "no-edge-150-locked-shadow", 257, 173, 144, false, true, true, true };
    }

    [Theory]
    [MemberData(nameof(RoundedCornerScenarios))]
    public void RoundedPinPixels_AreClippedAndPaintedContinuouslyAtAllFourCorners(
        string name,
        int width,
        int height,
        int dpi,
        bool border,
        bool shadow,
        bool locked,
        bool toolbarRequested)
    {
        _ = name;
        _ = shadow; // A native window shadow is outside the client bitmap and must not change its corner geometry.
        bool toolbarVisible = toolbarRequested && !locked;
        using var surface = RenderCornerScenario(width, height, dpi, border, toolbarVisible);

        AssertRoundedClipAtEveryCorner(surface, PinWindowGeometry.CornerRadius(dpi));
        if (border || toolbarVisible)
        {
            using var edge = RenderEdgeMask(width, height, dpi);
            AssertSingleConnectedEdgeTouchesEveryCorner(edge, PinWindowGeometry.CornerRadius(dpi));
        }
    }

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

    [Fact]
    public void RenderRoundedCornerMatrixToPng()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_PIN_CORNERS") != "1")
            return;

        string output = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-pin-corner-matrix.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        object[][] cases = RoundedCornerScenarios().ToArray();
        const int tileWidth = 360;
        const int tileHeight = 230;
        using var matrix = new SD.Bitmap(tileWidth * 2, tileHeight * 3);
        using var graphics = SD.Graphics.FromImage(matrix);
        graphics.Clear(SD.Color.FromArgb(20, 23, 28));

        for (int i = 0; i < cases.Length; i++)
        {
            object[] item = cases[i];
            string name = (string)item[0];
            int width = (int)item[1];
            int height = (int)item[2];
            int dpi = (int)item[3];
            bool border = (bool)item[4];
            bool shadow = (bool)item[5];
            bool locked = (bool)item[6];
            bool toolbar = (bool)item[7] && !locked;
            int x = i % 2 * tileWidth;
            int y = i / 2 * tileHeight;

            using var tile = RenderCornerScenario(
                Math.Min(width, tileWidth - 40),
                Math.Min(height, tileHeight - 62),
                dpi,
                border,
                toolbar);
            int imageX = x + (tileWidth - tile.Width) / 2;
            int imageY = y + 38 + (tileHeight - 48 - tile.Height) / 2;
            graphics.DrawImageUnscaled(tile, imageX, imageY);

            using var font = new SD.Font("Segoe UI", 11, SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
            string label = $"{name}  {dpi} DPI  shadow:{shadow}";
            graphics.DrawString(label, font, SD.Brushes.White, x + 12, y + 12);
        }

        matrix.Save(output, SD.Imaging.ImageFormat.Png);
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

    private static SD.Bitmap RenderCornerScenario(
        int width,
        int height,
        int dpi,
        bool border,
        bool toolbarVisible)
    {
        var bitmap = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = SD.Graphics.FromImage(bitmap);
        graphics.Clear(SD.Color.Transparent);
        using var clipPath = PinWindowGeometry.RoundedClipPath(bitmap.Size, dpi);
        using var clip = new SD.Region(clipPath);
        graphics.SetClip(clip, CombineMode.Replace);
        using (var fill = new SD.SolidBrush(SD.Color.FromArgb(38, 86, 112)))
            graphics.FillRectangle(fill, 0, 0, width, height);

        if (border)
        {
            using var pen = new SD.Pen(SD.Color.FromArgb(188, 198, 210), PinInteraction.ScaleLogical(1, dpi));
            PinWindowGeometry.DrawBorder(graphics, pen, bitmap.Size, dpi, roundedCorners: true);
        }

        if (toolbarVisible)
        {
            using var pen = new SD.Pen(SD.Color.FromArgb(10, 132, 255), PinInteraction.ScaleLogical(1, dpi));
            PinWindowGeometry.DrawBorder(graphics, pen, bitmap.Size, dpi, roundedCorners: true);
        }

        return bitmap;
    }

    private static SD.Bitmap RenderEdgeMask(int width, int height, int dpi)
    {
        var bitmap = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = SD.Graphics.FromImage(bitmap);
        graphics.Clear(SD.Color.Transparent);
        using var clipPath = PinWindowGeometry.RoundedClipPath(bitmap.Size, dpi);
        using var clip = new SD.Region(clipPath);
        graphics.SetClip(clip, CombineMode.Replace);
        using var pen = new SD.Pen(SD.Color.White, PinInteraction.ScaleLogical(1, dpi));
        PinWindowGeometry.DrawBorder(graphics, pen, bitmap.Size, dpi, roundedCorners: true);
        return bitmap;
    }

    private static void AssertRoundedClipAtEveryCorner(SD.Bitmap bitmap, int radius)
    {
        int block = Math.Max(2, Math.Min(Math.Min(bitmap.Width, bitmap.Height) / 2, radius + 2));
        foreach (SD.Rectangle corner in CornerBlocks(bitmap.Size, block))
        {
            int opaque = 0;
            int transparent = 0;
            for (int y = corner.Top; y < corner.Bottom; y++)
            for (int x = corner.Left; x < corner.Right; x++)
            {
                if (bitmap.GetPixel(x, y).A == 0)
                    transparent++;
                else
                    opaque++;
            }

            Assert.True(transparent > 0, $"Corner {corner} was not rounded.");
            Assert.True(opaque > 0, $"Corner {corner} was clipped away completely.");
        }
    }

    private static void AssertSingleConnectedEdgeTouchesEveryCorner(SD.Bitmap bitmap, int radius)
    {
        var edgePixels = new HashSet<SD.Point>();
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A >= 128)
                edgePixels.Add(new SD.Point(x, y));
        }

        Assert.NotEmpty(edgePixels);
        var remaining = new HashSet<SD.Point>(edgePixels);
        var queue = new Queue<SD.Point>();
        SD.Point first = remaining.First();
        remaining.Remove(first);
        queue.Enqueue(first);
        while (queue.Count > 0)
        {
            SD.Point point = queue.Dequeue();
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                var neighbor = new SD.Point(point.X + dx, point.Y + dy);
                if (remaining.Remove(neighbor))
                    queue.Enqueue(neighbor);
            }
        }

        Assert.True(remaining.Count == 0, $"Painted rounded edge split into disconnected segments ({remaining.Count} pixels outside the main edge).");

        int block = Math.Max(2, Math.Min(Math.Min(bitmap.Width, bitmap.Height) / 2, radius + 2));
        foreach (SD.Rectangle corner in CornerBlocks(bitmap.Size, block))
            Assert.Contains(edgePixels, point => corner.Contains(point));
    }

    private static IEnumerable<SD.Rectangle> CornerBlocks(SD.Size size, int block)
    {
        yield return new SD.Rectangle(0, 0, block, block);
        yield return new SD.Rectangle(size.Width - block, 0, block, block);
        yield return new SD.Rectangle(0, size.Height - block, block, block);
        yield return new SD.Rectangle(size.Width - block, size.Height - block, block, block);
    }

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
