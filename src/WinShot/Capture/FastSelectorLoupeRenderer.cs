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
        SD.Point cursorScreen)
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
            using var sample = CaptureService.CaptureScreenRegionWithoutLayeredWindows(loupe.SourceScreen);
            SD.Color pixel = ReadTargetPixel(sample, loupe.TargetSample);
            string hex = $"#{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}";
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

    private static SD.Color ReadTargetPixel(SD.Bitmap sample, SD.Point target)
    {
        int x = Math.Clamp(target.X, 0, sample.Width - 1);
        int y = Math.Clamp(target.Y, 0, sample.Height - 1);
        return sample.GetPixel(x, y);
    }

    private static void DrawSample(SD.Graphics g, SD.Bitmap sample, FastSelectorLoupe loupe)
    {
        var state = g.Save();
        using var path = new SD.Drawing2D.GraphicsPath();
        path.AddEllipse(loupe.Bounds);
        g.SetClip(path);
        g.InterpolationMode = SD.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(sample, loupe.Bounds);
        DrawPixelGrid(g, loupe);
        DrawTargetCell(g, loupe);
        g.Restore(state);
    }

    /// <summary>Faint 1px gridlines every <c>zoom</c> device pixels so individual
    /// source pixels are distinguishable in the magnified view (clip is the ellipse).</summary>
    private static void DrawPixelGrid(SD.Graphics g, FastSelectorLoupe loupe)
    {
        int zoom = loupe.Zoom;
        if (zoom < 4)
            return;

        var bounds = loupe.Bounds;
        using var grid = new SD.Pen(SD.Color.FromArgb(40, 0, 0, 0), 1);
        for (int x = bounds.Left; x <= bounds.Right; x += zoom)
            g.DrawLine(grid, x, bounds.Top, x, bounds.Bottom);
        for (int y = bounds.Top; y <= bounds.Bottom; y += zoom)
            g.DrawLine(grid, bounds.Left, y, bounds.Right, y);
    }

    /// <summary>Accent outline around the exact target pixel cell (CleanShot-style).</summary>
    private static void DrawTargetCell(SD.Graphics g, FastSelectorLoupe loupe)
    {
        int zoom = loupe.Zoom;
        var bounds = loupe.Bounds;
        int cellLeft = bounds.Left + loupe.TargetSample.X * zoom;
        int cellTop = bounds.Top + loupe.TargetSample.Y * zoom;
        var cell = new SD.Rectangle(cellLeft, cellTop, zoom, zoom);
        using var accent = new SD.Pen(ThemePalette.Accent, 1);
        g.DrawRectangle(accent, cell);
    }

    private static void DrawFrame(SD.Graphics g, FastSelectorLoupe loupe)
    {
        using var border = new SD.Pen(SD.Color.FromArgb(220, 255, 255, 255), 2);
        using var shadow = new SD.Pen(SD.Color.FromArgb(150, 0, 0, 0), 4);
        using var guide = new SD.Pen(SD.Color.FromArgb(220, ThemePalette.Accent), 1);
        g.DrawEllipse(shadow, loupe.Bounds);
        g.DrawEllipse(border, loupe.Bounds);
        g.DrawLine(guide, loupe.CrosshairCenter.X, loupe.Bounds.Top + 4, loupe.CrosshairCenter.X, loupe.Bounds.Bottom - 4);
        g.DrawLine(guide, loupe.Bounds.Left + 4, loupe.CrosshairCenter.Y, loupe.Bounds.Right - 4, loupe.CrosshairCenter.Y);
    }

    private static void DrawLabel(SD.Graphics g, SD.Size clientSize, FastSelectorLoupe loupe, string hex, SD.Color pixel)
    {
        const int Pad = 6;
        const int SwatchSize = 11;
        const int SwatchGap = 6;

        using var font = new SD.Font("Consolas", 8.5f);
        SD.Size coordSize = WF.TextRenderer.MeasureText(loupe.Coordinates, font);
        SD.Size hexSize = WF.TextRenderer.MeasureText(hex, font);

        int lineHeight = Math.Max(coordSize.Height, hexSize.Height);
        int textWidth = Math.Max(coordSize.Width, SwatchSize + SwatchGap + hexSize.Width);
        int boxW = textWidth + Pad * 2;
        int boxH = lineHeight * 2 + Pad * 2 + 2;

        int left = Math.Clamp(loupe.Bounds.Left + 18, 0, Math.Max(0, clientSize.Width - boxW));
        int top = Math.Clamp(loupe.Bounds.Bottom + 4, 0, Math.Max(0, clientSize.Height - boxH));
        var bg = new SD.Rectangle(left, top, boxW, boxH);
        using var brush = new SD.SolidBrush(SD.Color.FromArgb(220, 28, 28, 30));
        g.FillRectangle(brush, bg);

        int textLeft = left + Pad;
        int line1Top = top + Pad;
        WF.TextRenderer.DrawText(g, loupe.Coordinates, font, new SD.Point(textLeft, line1Top), SD.Color.White);

        int line2Top = line1Top + lineHeight + 2;
        var swatch = new SD.Rectangle(textLeft, line2Top + (lineHeight - SwatchSize) / 2, SwatchSize, SwatchSize);
        using (var swatchBrush = new SD.SolidBrush(SD.Color.FromArgb(255, pixel.R, pixel.G, pixel.B)))
            g.FillRectangle(swatchBrush, swatch);
        using (var swatchBorder = new SD.Pen(SD.Color.FromArgb(160, 255, 255, 255), 1))
            g.DrawRectangle(swatchBorder, swatch);
        WF.TextRenderer.DrawText(g, hex, font, new SD.Point(textLeft + SwatchSize + SwatchGap, line2Top), SD.Color.White);
    }
}
