using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// The magnifier loupe. EVERY constant in here is LOGICAL px and goes through <see cref="Px"/>
/// before it is drawn - the whole file used to consume them raw, which made the loupe the one
/// selector surface that did not grow with the monitor: 120 device px on a 150 % display is
/// 80 logical, two thirds of the 120 every sibling produces, with 5.3-logical-px zoom cells and
/// a pointer offset of 16 instead of 24. Its labels were Point fonts on top of that, so the text
/// scaled with the primary monitor while the box holding it scaled with nothing.
/// </summary>
internal static class FastSelectorLoupeRenderer
{
    private const int LoupeSize = 120;
    private const int LoupeZoom = 8;
    private static bool _failureLogged;

    private static int Px(int logical, double scale) =>
        WinShot.Recording.RecordingMonitorDpi.Px(logical, scale is > 0d and < 100d ? scale : 1d);

    /// <summary>Stroke width for the loupe's hairlines, rounded to whole pixels: these are drawn
    /// with AntiAlias, but a sub-pixel pen on a 150 % display reads as a blur, not a line.</summary>
    private static float Stroke(double logical, double scale) =>
        (float)Math.Max(1d, Math.Round(logical * (scale is > 0d and < 100d ? scale : 1d)));

    public static void Draw(
        SD.Graphics g,
        SD.Size clientSize,
        SD.Rectangle virtualScreen,
        SD.Point cursorClient,
        SD.Point cursorScreen,
        SD.Bitmap? frozen = null,
        SD.Size dragSize = default,
        double scale = 1d)
    {
        var loupe = FastSelectorLoupeLayout.Calculate(
            clientSize,
            virtualScreen,
            cursorClient,
            cursorScreen,
            Px(LoupeSize, scale),
            Px(LoupeZoom, scale),
            dragSize,
            scale);
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
            DrawShadow(g, loupe, scale);
            DrawSample(g, sample, loupe, scale);
            DrawFrame(g, loupe, scale);
            DrawLabel(g, clientSize, loupe, hex, pixel, scale);
        }
        catch (Exception ex)
        {
            if (_failureLogged)
                return;

            _failureLogged = true;
            Log.Error("Fast selector loupe draw failed", ex);
        }
    }

    /// <summary>
    /// The ground the loupe covers — its box plus the coordinate/hex pill under it — so
    /// chrome painted afterwards can step aside instead of blotting out the tool. Empty when
    /// the loupe is not showing. The pill is always narrower than the loupe (a coordinate
    /// pair at caption size is well under 120 px), so only its height has to be worked out.
    /// </summary>
    public static SD.Rectangle OccupiedRect(
        SD.Size clientSize, SD.Rectangle virtualScreen, SD.Point cursorClient, SD.Point cursorScreen,
        double scale = 1d)
    {
        var loupe = FastSelectorLoupeLayout.Calculate(
            clientSize, virtualScreen, cursorClient, cursorScreen,
            Px(LoupeSize, scale), Px(LoupeZoom, scale), default, scale);
        if (!loupe.IsVisible)
            return SD.Rectangle.Empty;

        int pillH = LabelHeight(scale);
        int pillTop = Math.Clamp(
            loupe.Bounds.Bottom + Px(PillGap, scale), 0, Math.Max(0, clientSize.Height - pillH));
        return SD.Rectangle.FromLTRB(
            loupe.Bounds.Left, loupe.Bounds.Top, loupe.Bounds.Right, pillTop + pillH);
    }

    /// <summary>How far the drop shadow's outermost ring reaches past the box, in LOGICAL px.
    /// <see cref="DrawShadow"/> walks i = 5..1 and inflates by <c>Px(i, scale)</c>.</summary>
    private const int ShadowReach = 5;

    /// <summary>
    /// Every pixel a repaint has to cover to ERASE the loupe: its box, its label pill and its
    /// drop shadow, in the caller's client space.
    ///
    /// This exists so the dirty rect and the drawing come from one calculation. The selector
    /// invalidated a flat 250-px half-box around the cursor in logical px while the loupe grew
    /// with the monitor, so from 125 % up the bottom of the box, the whole pill and the shadow
    /// fell outside the repaint — the loupe left a trail of its own outer band everywhere the
    /// pointer had been. At 192 dpi the box alone overran that half-box by 94 px.
    /// </summary>
    public static SD.Rectangle DirtyRect(
        SD.Size clientSize, SD.Rectangle virtualScreen, SD.Point cursorClient, SD.Point cursorScreen,
        double scale = 1d)
    {
        SD.Rectangle occupied = OccupiedRect(clientSize, virtualScreen, cursorClient, cursorScreen, scale);
        if (occupied.IsEmpty)
            return SD.Rectangle.Empty;

        // The shadow's outermost ring plus its pen width, and the one logical px it is nudged
        // down by. Applied on all four sides: over-invalidating a few px costs a repaint, and
        // under-invalidating by one leaves a hairline of the old loupe on screen.
        int margin = Px(ShadowReach, scale) + (int)Stroke(2, scale) + Px(1, scale);
        occupied.Inflate(margin, margin);
        return occupied;
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
    private static void DrawShadow(SD.Graphics g, FastSelectorLoupe loupe, double scale)
    {
        var prevSmoothing = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        for (int i = 5; i >= 1; i--)
        {
            int ring = Px(i, scale);
            var rect = loupe.Bounds;
            rect.Inflate(ring, ring);
            rect.Offset(0, Px(1, scale));
            int alpha = 14 - i * 2; // ~4..12, fades outward
            using var pen = new SD.Pen(SD.Color.FromArgb(Math.Max(2, alpha), 0, 0, 0), Stroke(2, scale));
            using var path = GdiPaths.RoundedRect(rect, loupe.CornerRadius + ring);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = prevSmoothing;
    }

    private static void DrawSample(
        SD.Graphics g, SD.Bitmap sample, FastSelectorLoupe loupe, double scale)
    {
        var state = g.Save();
        using var clip = GdiPaths.RoundedRect(loupe.Bounds, loupe.CornerRadius);
        g.SetClip(clip);
        g.InterpolationMode = SD.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(sample, loupe.Bounds);
        DrawPixelGrid(g, loupe);
        DrawTargetCell(g, loupe, scale);
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
    private static void DrawTargetCell(SD.Graphics g, FastSelectorLoupe loupe, double scale)
    {
        int zoom = loupe.Zoom;
        var bounds = loupe.Bounds;
        int cellLeft = bounds.Left + loupe.TargetSample.X * zoom;
        int cellTop = bounds.Top + loupe.TargetSample.Y * zoom;
        var cell = new SD.Rectangle(cellLeft, cellTop, zoom, zoom);
        using var accent = new SD.Pen(SD.Color.FromArgb(235, ThemePalette.Accent), Stroke(1.5, scale));
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

        // SurfaceHover at 150 alpha. #5A5A5C matched no token at all; the crosshair inside a
        // loupe is a raised neutral like every other raised neutral in the app.
        using var bar = new SD.Pen(SD.Color.FromArgb(150, ThemePalette.SurfaceHover), thickness);
        // Vertical bar (split around the center cell).
        g.DrawLine(bar, cx, bounds.Top, cx, cy - gap);
        g.DrawLine(bar, cx, cy + gap, cx, bounds.Bottom);
        // Horizontal bar (split around the center cell).
        g.DrawLine(bar, bounds.Left, cy, cx - gap, cy);
        g.DrawLine(bar, cx + gap, cy, bounds.Right, cy);
    }

    private static void DrawFrame(SD.Graphics g, FastSelectorLoupe loupe, double scale)
    {
        var prevSmoothing = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;

        // Subtle dark outer border + a faint inner light hairline so the loupe reads
        // crisply against both light and dark desktop content (CleanShot style).
        // WindowBg written as a token, not as the same three bytes in decimal — this file
        // already spells it that way 46 lines down.
        using var border = new SD.Pen(SD.Color.FromArgb(190, ThemePalette.WindowBg), Stroke(1.5, scale));
        using (var path = GdiPaths.RoundedRect(loupe.Bounds, loupe.CornerRadius))
            g.DrawPath(border, path);

        var inner = loupe.Bounds;
        int hairline = Px(1, scale);
        inner.Inflate(-hairline, -hairline);
        using var innerLine = new SD.Pen(SD.Color.FromArgb(60, 255, 255, 255), Stroke(1, scale));
        using (var innerPath = GdiPaths.RoundedRect(inner, Math.Max(2, loupe.CornerRadius - hairline)))
            g.DrawPath(innerLine, innerPath);

        g.SmoothingMode = prevSmoothing;
    }

    /// <summary>Coordinate + hex readout in a small dark pill — the same material as
    /// <see cref="SelectorChrome.DrawLabel"/>'s size pill — so the loupe's payload stays
    /// readable over light desktop content, where shadowed bare text washes out.</summary>
    private const int SwatchSize = 10;
    private const int SwatchGap = 6;
    private const int LineGap = 3;
    private const int Pad = 6;
    private const int PillRadius = 6;
    /// <summary>Gap between the loupe box and its label pill, and the pill's left nudge.</summary>
    private const int PillGap = 6;
    private const int PillNudgeX = 2;

    /// <summary>The caption font in PIXELS for this monitor. A Point font resolves against the
    /// primary display's DPI, so the label grew while the box around it did not.</summary>
    private static SD.Font CaptionFont(double scale) =>
        ThemePalette.UiFontPx(ThemePalette.PxForPt(ThemePalette.TextCaptionPt, scale));

    /// <summary>The label pill's height, which is two caption lines whatever they say.</summary>
    private static int LabelHeight(double scale)
    {
        using var font = CaptionFont(scale);
        int lineHeight = Math.Max(WF.TextRenderer.MeasureText("0", font).Height, Px(SwatchSize, scale));
        return (lineHeight * 2) + Px(LineGap, scale) + (Px(Pad, scale) * 2);
    }

    private static void DrawLabel(
        SD.Graphics g, SD.Size clientSize, FastSelectorLoupe loupe, string hex, SD.Color pixel, double scale)
    {
        using var font = CaptionFont(scale);
        int swatchSize = Px(SwatchSize, scale);
        int swatchGap = Px(SwatchGap, scale);
        int lineGap = Px(LineGap, scale);
        int pad = Px(Pad, scale);
        SD.Size coordSize = WF.TextRenderer.MeasureText(loupe.Coordinates, font);
        SD.Size hexSize = WF.TextRenderer.MeasureText(hex, font);

        int lineHeight = Math.Max(coordSize.Height, Math.Max(hexSize.Height, swatchSize));
        int blockW = Math.Max(coordSize.Width, swatchSize + swatchGap + hexSize.Width);
        int pillW = blockW + pad * 2;
        int pillH = lineHeight * 2 + lineGap + pad * 2;

        int pillLeft = Math.Clamp(
            loupe.Bounds.Left + Px(PillNudgeX, scale), 0, Math.Max(0, clientSize.Width - pillW));
        int pillTop = Math.Clamp(
            loupe.Bounds.Bottom + Px(PillGap, scale), 0, Math.Max(0, clientSize.Height - pillH));

        var prevSmoothing = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(
            new SD.Rectangle(pillLeft, pillTop, pillW, pillH), Px(PillRadius, scale)))
        using (var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(235, ThemePalette.WindowBg)))
            g.FillPath(bgBrush, path);
        g.SmoothingMode = prevSmoothing;

        int left = pillLeft + pad;
        int top = pillTop + pad;

        // Line 1: coordinates.
        WF.TextRenderer.DrawText(g, loupe.Coordinates, font, new SD.Point(left, top), ThemePalette.TextPrimary);

        // Line 2: hex swatch + value.
        int line2Top = top + lineHeight + lineGap;
        var swatch = new SD.Rectangle(left, line2Top + (lineHeight - swatchSize) / 2, swatchSize, swatchSize);
        using (var swatchBrush = new SD.SolidBrush(SD.Color.FromArgb(255, pixel.R, pixel.G, pixel.B)))
            g.FillRectangle(swatchBrush, swatch);
        using (var swatchBorder = new SD.Pen(SD.Color.FromArgb(200, 0, 0, 0), Stroke(1, scale)))
            g.DrawRectangle(swatchBorder, swatch);
        WF.TextRenderer.DrawText(
            g, hex, font, new SD.Point(left + swatchSize + swatchGap, line2Top), ThemePalette.TextPrimary);
    }
}
