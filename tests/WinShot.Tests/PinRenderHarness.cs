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

    // ------------------------------------------------------------------
    // Pixel evidence. Three of this track's claims are claims about pixels --
    // the image bleeding to its own edge, the chrome fading rather than
    // popping, and the locked dim landing on the image and not on the chips --
    // and HANDOFF is explicit that eyeballing a render PNG has been wrong here
    // twice. These scan the surface instead.
    // ------------------------------------------------------------------

    /// <summary>
    /// The pinned image runs to the window's own edge. It used to be drawn inset by one over
    /// a black BackColor, which baked a dark hairline around every pin -- visible on a light
    /// screenshot even with PinnedBorder off.
    /// </summary>
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void APinnedImageReachesItsOwnEdgeWithNoBakedFrame(int dpi)
    {
        using var pin = CreatePinForPixelScan(300, 200, dpi, border: false);
        using SD.Bitmap surface = PaintPin(pin);

        SD.Color expected = SD.Color.FromArgb(238, 244, 250);
        int middle = surface.Height / 2;
        int quarter = surface.Width / 4;

        AssertSameColor(expected, surface.GetPixel(0, middle), "left column");
        AssertSameColor(expected, surface.GetPixel(surface.Width - 1, middle), "right column");
        AssertSameColor(expected, surface.GetPixel(quarter, surface.Height - 1), "bottom row");
        // The top row under the toolbar's own x range is chrome; sample beside it.
        AssertSameColor(expected, surface.GetPixel(2, 0), "top row");
    }

    /// <summary>
    /// The hover chrome is drawn at <c>_chromeAlpha</c>, so a half-run fade has to be visibly
    /// half-drawn. A bar that composited at full strength for any alpha above zero would be a
    /// pop wearing a tween's timing.
    /// </summary>
    [Fact]
    public void TheHoverChromeIsDrawnAtItsFadeAmount()
    {
        using var pin = CreatePinForPixelScan(300, 200, 96, border: false);
        SD.Point probe = BarCentre(pin);

        int gone = BrightnessAt(pin, 0d, probe);
        int half = BrightnessAt(pin, 0.5d, probe);
        int up = BrightnessAt(pin, 1d, probe);

        Assert.Equal(238, gone);                       // no chrome at all: the capture itself
        Assert.True(half < gone - 20, $"half-faded bar {half} is not visibly down from {gone}");
        Assert.True(up < half - 20, $"fully drawn bar {up} is not darker than half {half}");
    }

    /// <summary>
    /// The zoom chip hangs below the toolbar, and the chrome fade repaints only
    /// ChromeInvalidationBounds. When the chip fell outside that rect it was painted once, at
    /// whatever alpha the first full repaint caught, and then frozen there for the hover.
    /// </summary>
    [Fact]
    public void TheZoomChipIsInsideTheRectTheChromeFadeRepaints()
    {
        using var pin = CreatePinForPixelScan(600, 400, 96, border: false);
        var type = typeof(FastPinWindow);
        var bar = (SD.Rectangle)Invoke(pin, "ToolbarBounds");
        var chip = (SD.Rectangle)type
            .GetMethod("ZoomChipBounds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, new object[] { bar })!;
        var invalid = (SD.Rectangle)Invoke(pin, "ChromeInvalidationBounds");

        Assert.False(chip.IsEmpty, "a 600x400 pin has room for the chip");
        Assert.True(invalid.Contains(chip),
            $"chip {chip} is not inside the chrome repaint rect {invalid} (bar {bar})");

        // And it is actually painted there, at the fade's amount.
        var centre = new SD.Point(chip.Left + chip.Width / 2, chip.Top + chip.Height / 2);
        Assert.Equal(238, BrightnessAt(pin, 0d, centre));
        Assert.True(BrightnessAt(pin, 1d, centre) < 200, "the chip did not paint over the capture");
    }

    /// <summary>
    /// A pin narrower than its own toolbar drops chips from the left rather than painting
    /// them off the form. Lock and Close are the pin's only ways out, so they are the two that
    /// stay -- and the bar has to stay inside the client at every DPI, not just at 100 %.
    /// </summary>
    [Theory]
    [InlineData(120, 96)]
    [InlineData(150, 144)]
    [InlineData(90, 192)]
    [InlineData(600, 96)]
    public void TheToolbarNeverOverflowsTheClientArea(int width, int dpi)
    {
        using var pin = CreatePinForPixelScan(width, 140, dpi, border: false);
        var bar = (SD.Rectangle)Invoke(pin, "ToolbarBounds");
        var offered = (int[])typeof(FastPinWindow)
            .GetProperty("VisibleButtons", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pin)!;

        Assert.True(bar.Left >= 0, $"bar {bar} starts off the left of {pin.ClientSize}");
        Assert.True(bar.Right <= pin.ClientSize.Width || offered.Length == 1,
            $"bar {bar} overflows a {pin.ClientSize} pin with {offered.Length} chips");

        // Every chip the state offers is reachable: the same rects HitTestButton reads.
        var buttonBounds = typeof(FastPinWindow)
            .GetMethod("ButtonBounds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (int index in offered)
        {
            var rect = (SD.Rectangle)buttonBounds.Invoke(pin, new object[] { index })!;
            Assert.True(rect.Right <= pin.ClientSize.Width || offered.Length == 1,
                $"chip {index} at {rect} is outside a {pin.ClientSize} pin");
        }

        // Close is always the last chip standing.
        Assert.Contains(4, offered);
    }

    /// <summary>
    /// A locked pin dims the image under the pointer and leaves the chips over it at full
    /// strength -- that is the whole reason the dim is an alpha matrix on the bitmap rather
    /// than Form.Opacity, which would take the chips down with it.
    /// </summary>
    [Fact]
    public void ALockedPinDimsItsImageAndNotItsChips()
    {
        using var pin = CreatePinForPixelScan(300, 200, 96, border: false);
        SetField(pin, "_locked", true);
        SetField(pin, "_mouseInside", true);
        SetField(pin, "_chromeAlpha", 1d);

        SD.Point probe = BarCentre(pin);
        int lit = BrightnessAt(pin, 1d, probe);

        SetField(pin, "_imageAlpha", PinInteraction.LockedOpacity(1d));
        using SD.Bitmap dimmed = PaintPin(pin);

        int image = dimmed.GetPixel(4, dimmed.Height - 4).R;
        int expected = (int)Math.Round(238 * PinInteraction.LockedOpacity(1d));
        Assert.True(Math.Abs(image - expected) <= 2,
            $"a locked hovered pin painted its image at {image}, not the dimmed {expected}");
        // The bar is 235/255, so a sliver of the dimmed capture reads through it -- but only a
        // sliver. Form.Opacity, which is what this replaced, would have taken the chips down
        // with the image.
        int bar = dimmed.GetPixel(probe.X, probe.Y).R;
        Assert.True(Math.Abs(lit - bar) <= 6, $"the chrome dimmed too: {lit} -> {bar}");
        Assert.True(238 - image > (lit - bar) * 4,
            $"the dim landed on the chrome ({lit}->{bar}) as much as on the image (238->{image})");
    }

    private static void AssertSameColor(SD.Color expected, SD.Color actual, string where)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= 2 &&
            Math.Abs(expected.G - actual.G) <= 2 &&
            Math.Abs(expected.B - actual.B) <= 2,
            $"{where}: expected the capture {expected} but the pin painted {actual}");
    }

    /// <summary>A pin at a known client size over a flat, bright capture, so any darkening in
    /// the surface came from the pin's own chrome.</summary>
    private static FastPinWindow CreatePinForPixelScan(int width, int height, int dpi, bool border)
    {
        var capture = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(capture))
            g.Clear(SD.Color.FromArgb(238, 244, 250));

        var settings = new SettingsService();
        settings.Current.PinnedRoundedCorners = true;
        settings.Current.PinnedShadow = false;
        settings.Current.PinnedBorder = border;

        var pin = new FastPinWindow(capture, settings);
        pin.CreateControl();
        // Scale and DPI pinned by hand: AutoScaleMode.Dpi never scales for the creation
        // monitor, and the run host's DPI is not the DPI under test.
        SetField(pin, "_scale", 1d);
        typeof(WF.Control).GetProperty("DeviceDpi", BindingFlags.Instance | BindingFlags.Public)!
            .GetSetMethod(true)?.Invoke(pin, new object[] { dpi });
        typeof(FastPinWindow).GetMethod("ApplyScale", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, null);
        pin.ClientSize = new SD.Size(width, height);
        return pin;
    }

    private static object Invoke(FastPinWindow pin, string method)
        => typeof(FastPinWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, null)!;

    private static SD.Point BarCentre(FastPinWindow pin)
    {
        var bar = (SD.Rectangle)Invoke(pin, "ToolbarBounds");
        return new SD.Point(bar.Left + 2, bar.Top + bar.Height / 2);
    }

    private static int BrightnessAt(FastPinWindow pin, double chromeAlpha, SD.Point at)
    {
        SetField(pin, "_chromeAlpha", chromeAlpha);
        using SD.Bitmap surface = PaintPin(pin);
        return surface.GetPixel(at.X, at.Y).R;
    }

    private static SD.Bitmap PaintPin(FastPinWindow pin)
    {
        var bitmap = new SD.Bitmap(pin.ClientSize.Width, pin.ClientSize.Height,
            SD.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = SD.Graphics.FromImage(bitmap);
        // Opaque black ground: the pin paints onto the desktop, and measuring it over a
        // transparent bitmap reads back the compositing rather than the pixels a person sees.
        graphics.Clear(SD.Color.Black);
        using var args = new WF.PaintEventArgs(graphics, new SD.Rectangle(SD.Point.Empty, bitmap.Size));
        typeof(FastPinWindow).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, new object[] { args });
        return bitmap;
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
                // OnPaint gates the whole toolbar on _chromeAlpha and fades every chrome
                // brush through it. Without this the render is a bare capture: no toolbar,
                // no zoom chip, no focus ring -- which is exactly the render evidence this
                // artifact exists to carry.
                SetField(pin, "_chromeAlpha", 1d);
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
        AssertToolbarBandIsPresent(output);
    }

    /// <summary>
    /// The artifact is only evidence if the chrome it is meant to show is actually in it.
    /// A missing <c>_chromeAlpha</c> once produced a bare capture that looked perfectly
    /// fine to a human -- HANDOFF's "never eyeball this" case -- so the band is counted.
    /// </summary>
    private static void AssertToolbarBandIsPresent(string path)
    {
        using var png = new SD.Bitmap(path);
        int bandRows = 0;
        for (int y = 0; y < png.Height; y++)
        {
            int neutral = 0;
            for (int x = 0; x < png.Width; x++)
            {
                SD.Color c = png.GetPixel(x, y);
                // The bar is 235/255 of ToolbarBg over the capture: dark and near-neutral,
                // which nothing in the synthetic gradient capture is.
                if (c.A > 200 && Math.Abs(c.R - c.G) < 8 && Math.Abs(c.G - c.B) < 8 &&
                    c.R > 25 && c.R < 90)
                    neutral++;
            }
            if (neutral > 30)
                bandRows++;
        }
        Assert.True(bandRows > 8,
            $"{path} carries no toolbar band ({bandRows} rows of near-neutral chrome) - " +
            "the render painted a bare capture.");
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

        // The focus ring is part of OnPaint now (DrawButton reads _focusButton), so the
        // caller sets the fields and this just paints. The old overlay reflected into a
        // static DrawButton and a GlyphFor that no longer exist.
        _ = showKeyboardFocus;
        using var args = new WF.PaintEventArgs(graphics, new SD.Rectangle(SD.Point.Empty, bitmap.Size));
        typeof(FastPinWindow)
            .GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, new object[] { args });
        bitmap.Save(path, SD.Imaging.ImageFormat.Png);
    }

    internal static void SetField(FastPinWindow pin, string name, object value)
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
