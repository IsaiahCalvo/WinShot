using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

internal static class FastSelectorLoupeRenderer
{
    private const int LoupeSize = 120;
    private const int LoupeZoom = 8;
    private static bool _failureLogged;

    public static void Draw(
        SD.Graphics g,
        SD.Size clientSize,
        SD.Rectangle virtualScreen,
        SD.Point cursorClient,
        SD.Point cursorScreen,
        SD.Bitmap? frozen = null)
    {
        var loupe = FastSelectorLoupeLayout.Calculate(
            clientSize,
            virtualScreen,
            cursorClient,
            cursorScreen,
            LoupeSize,
            LoupeZoom);
        if (!loupe.IsVisible)
            return;

        try
        {
            // With screen-freeze, sample from the frozen desktop bitmap so the loupe shows
            // the same still pixels the user is selecting (and avoids a live BitBlt per paint).
            using var sample = frozen is not null
                ? CropFrozenSample(frozen, loupe.SourceScreen, virtualScreen)
                : CaptureService.CaptureScreenRegionWithoutLayeredWindows(loupe.SourceScreen);
            SD.Color pixel = ReadTargetPixel(sample, loupe.TargetSample);
            string hex = $"#{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}";
            DrawShadow(g, loupe);
            DrawSample(g, sample, loupe);
            DrawFrame(g, loupe);
            DrawLabel(g, clientSize, loupe, hex, pixel);
        }
        catch (Exception ex)
        {
            if (_failureLogged)
                return;

            _failureLogged = true;
            Log.Error("Fast selector loupe draw failed", ex);
        }
    }

    /// <summary>Crops the loupe's source region out of the frozen virtual-desktop bitmap,
    /// matching the size the live-capture path would have returned.</summary>
    private static SD.Bitmap CropFrozenSample(SD.Bitmap frozen, SD.Rectangle sourceScreen, SD.Rectangle virtualScreen)
    {
        var crop = new SD.Bitmap(
            Math.Max(1, sourceScreen.Width),
            Math.Max(1, sourceScreen.Height),
            SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(crop);
        var src = new SD.Rectangle(
            sourceScreen.X - virtualScreen.X,
            sourceScreen.Y - virtualScreen.Y,
            sourceScreen.Width,
            sourceScreen.Height);
        g.DrawImage(frozen, new SD.Rectangle(0, 0, crop.Width, crop.Height), src, SD.GraphicsUnit.Pixel);
        return crop;
    }

    private static SD.Color ReadTargetPixel(SD.Bitmap sample, SD.Point target)
    {
        int x = Math.Clamp(target.X, 0, sample.Width - 1);
        int y = Math.Clamp(target.Y, 0, sample.Height - 1);
        return sample.GetPixel(x, y);
    }

    /// <summary>Soft drop shadow under the loupe, drawn as a few concentric rounded
    /// rectangles with increasing transparency to fake a blur.</summary>
    private static void DrawShadow(SD.Graphics g, FastSelectorLoupe loupe)
    {
        var prevSmoothing = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        for (int i = 5; i >= 1; i--)
        {
            var rect = loupe.Bounds;
            rect.Inflate(i, i);
            rect.Offset(0, 1);
            int alpha = 14 - i * 2; // ~4..12, fades outward
            using var pen = new SD.Pen(SD.Color.FromArgb(Math.Max(2, alpha), 0, 0, 0), 2f);
            using var path = GdiPaths.RoundedRect(rect, loupe.CornerRadius + i);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = prevSmoothing;
    }

    private static void DrawSample(SD.Graphics g, SD.Bitmap sample, FastSelectorLoupe loupe)
    {
        var state = g.Save();
        using var clip = GdiPaths.RoundedRect(loupe.Bounds, loupe.CornerRadius);
        g.SetClip(clip);
        g.InterpolationMode = SD.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(sample, loupe.Bounds);
        DrawPixelGrid(g, loupe);
        DrawTargetCell(g, loupe);
        DrawCrosshair(g, loupe);
        g.Restore(state);
    }

    /// <summary>Faint 1px gridlines every <c>zoom</c> device pixels so individual
    /// source pixels are distinguishable in the magnified view (clip is the rounded square).</summary>
    private static void DrawPixelGrid(SD.Graphics g, FastSelectorLoupe loupe)
    {
        int zoom = loupe.Zoom;
        if (zoom < 4)
            return;

        var bounds = loupe.Bounds;
        using var grid = new SD.Pen(SD.Color.FromArgb(28, 0, 0, 0), 1);
        for (int x = bounds.Left; x <= bounds.Right; x += zoom)
            g.DrawLine(grid, x, bounds.Top, x, bounds.Bottom);
        for (int y = bounds.Top; y <= bounds.Bottom; y += zoom)
            g.DrawLine(grid, bounds.Left, y, bounds.Right, y);
    }

    /// <summary>Accent outline around the exact target pixel cell (WinShot precision cue,
    /// kept subtle so it reads inside CleanShot's gray crosshair).</summary>
    private static void DrawTargetCell(SD.Graphics g, FastSelectorLoupe loupe)
    {
        int zoom = loupe.Zoom;
        var bounds = loupe.Bounds;
        int cellLeft = bounds.Left + loupe.TargetSample.X * zoom;
        int cellTop = bounds.Top + loupe.TargetSample.Y * zoom;
        var cell = new SD.Rectangle(cellLeft, cellTop, zoom, zoom);
        using var accent = new SD.Pen(SD.Color.FromArgb(235, ThemePalette.Accent), 1.5f);
        g.DrawRectangle(accent, cell);
    }

    /// <summary>CleanShot's thin gray crosshair through the center pixel: two
    /// semi-transparent gray bars that frame the target cell, leaving it visible.</summary>
    private static void DrawCrosshair(SD.Graphics g, FastSelectorLoupe loupe)
    {
        var bounds = loupe.Bounds;
        int zoom = loupe.Zoom;
        int cx = loupe.CrosshairCenter.X;
        int cy = loupe.CrosshairCenter.Y;
        // Bar thickness tracks the pixel-cell size so the crosshair frames one cell.
        float thickness = Math.Max(2f, zoom * 0.5f);
        int gap = zoom; // keep the center pixel clear

        using var bar = new SD.Pen(SD.Color.FromArgb(150, 90, 90, 92), thickness);
        // Vertical bar (split around the center cell).
        g.DrawLine(bar, cx, bounds.Top, cx, cy - gap);
        g.DrawLine(bar, cx, cy + gap, cx, bounds.Bottom);
        // Horizontal bar (split around the center cell).
        g.DrawLine(bar, bounds.Left, cy, cx - gap, cy);
        g.DrawLine(bar, cx + gap, cy, bounds.Right, cy);
    }

    private static void DrawFrame(SD.Graphics g, FastSelectorLoupe loupe)
    {
        var prevSmoothing = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;

        // Subtle dark outer border + a faint inner light hairline so the loupe reads
        // crisply against both light and dark desktop content (CleanShot style).
        using var border = new SD.Pen(SD.Color.FromArgb(190, 28, 28, 30), 1.5f);
        using (var path = GdiPaths.RoundedRect(loupe.Bounds, loupe.CornerRadius))
            g.DrawPath(border, path);

        var inner = loupe.Bounds;
        inner.Inflate(-1, -1);
        using var innerLine = new SD.Pen(SD.Color.FromArgb(60, 255, 255, 255), 1f);
        using (var innerPath = GdiPaths.RoundedRect(inner, Math.Max(2, loupe.CornerRadius - 1)))
            g.DrawPath(innerLine, innerPath);

        g.SmoothingMode = prevSmoothing;
    }

    /// <summary>CleanShot shows a plain light coordinate readout near the loupe (no heavy
    /// pill). WinShot adds a small hex swatch; both are drawn as light text with a soft
    /// dark shadow for legibility over any background.</summary>
    private static void DrawLabel(SD.Graphics g, SD.Size clientSize, FastSelectorLoupe loupe, string hex, SD.Color pixel)
    {
        const int SwatchSize = 10;
        const int SwatchGap = 6;
        const int LineGap = 3;

        using var font = ThemePalette.UiFont(8.25f);
        SD.Size coordSize = WF.TextRenderer.MeasureText(loupe.Coordinates, font);
        SD.Size hexSize = WF.TextRenderer.MeasureText(hex, font);

        int lineHeight = Math.Max(coordSize.Height, Math.Max(hexSize.Height, SwatchSize));
        int blockW = Math.Max(coordSize.Width, SwatchSize + SwatchGap + hexSize.Width);
        int blockH = lineHeight * 2 + LineGap;

        int left = Math.Clamp(loupe.Bounds.Left + 2, 0, Math.Max(0, clientSize.Width - blockW));
        int top = Math.Clamp(loupe.Bounds.Bottom + 6, 0, Math.Max(0, clientSize.Height - blockH));

        // Line 1: coordinates (plain light text, soft shadow).
        DrawShadowedText(g, loupe.Coordinates, font, new SD.Point(left, top));

        // Line 2: hex swatch + value.
        int line2Top = top + lineHeight + LineGap;
        var swatch = new SD.Rectangle(left, line2Top + (lineHeight - SwatchSize) / 2, SwatchSize, SwatchSize);
        using (var swatchBrush = new SD.SolidBrush(SD.Color.FromArgb(255, pixel.R, pixel.G, pixel.B)))
            g.FillRectangle(swatchBrush, swatch);
        using (var swatchBorder = new SD.Pen(SD.Color.FromArgb(200, 0, 0, 0), 1))
            g.DrawRectangle(swatchBorder, swatch);
        DrawShadowedText(g, hex, font, new SD.Point(left + SwatchSize + SwatchGap, line2Top));
    }

    /// <summary>Draws light text with a 1px dark shadow so it stays readable over any
    /// desktop content without a background pill.</summary>
    private static void DrawShadowedText(SD.Graphics g, string text, SD.Font font, SD.Point origin)
    {
        var shadow = SD.Color.FromArgb(180, 0, 0, 0);
        WF.TextRenderer.DrawText(g, text, font, new SD.Point(origin.X + 1, origin.Y + 1), shadow);
        WF.TextRenderer.DrawText(g, text, font, origin, ThemePalette.TextPrimary);
    }
}
