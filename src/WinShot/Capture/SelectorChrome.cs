using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Shared visual chrome for the capture selectors (<see cref="FastRegionSelectorDialog"/>
/// and <see cref="FastAllInOneSelectorDialog"/>): the crosshair guide lines, the
/// size/coordinate label pill, the measurement vocabulary, and the common overlay-surface
/// setup. Kept in one place so both selectors draw identical chrome and a styling change
/// happens once.
/// </summary>
internal static class SelectorChrome
{
    private const int CrosshairGapPx = 10;
    private const double LiveDesktopOpacity = 0.45;

    /// <summary>LiveDesktopOpacity as a layered-window alpha, for SetLayeredAlpha resets
    /// on pooled surfaces whose alpha was flipped to 255 by a previous frozen swap.</summary>
    internal const byte LiveAlpha = (byte)(LiveDesktopOpacity * 255);

    /// <summary>Shared overlay-surface setup for a selector coordinator form or one of its panes.</summary>
    public static void ConfigureSurface(WF.Form form)
    {
        form.AutoScaleMode = WF.AutoScaleMode.None;
        form.BackColor = SD.Color.Black;
        form.Cursor = WF.Cursors.Cross;
        form.FormBorderStyle = WF.FormBorderStyle.None;
        form.KeyPreview = true;
        form.ShowInTaskbar = false;
        form.StartPosition = WF.FormStartPosition.Manual;
        form.TopMost = true;
        // The selector opens BEFORE the freeze snapshot is taken (so the hotkey feels
        // instant); exclusion keeps these surfaces out of that deferred snapshot and out
        // of any confirm-time live grab. WGC/Desktop Duplication honor display affinity;
        // the BitBlt tier omits CAPTUREBLT, so the translucent (layered) surface is
        // skipped there by construction.
        form.HandleCreated += (_, _) => WinShot.Scrolling.CaptureExclusion.Apply(form.Handle);
    }

    /// <summary>Freeze-on surfaces are opaque snapshots. Freeze-off surfaces are native
    /// translucent overlays, leaving the changing desktop visible without background work.</summary>
    public static void ConfigurePresentation(WF.Form form, bool freezeScreen)
    {
        form.Opacity = freezeScreen ? 1.0 : LiveDesktopOpacity;
    }

    /// <summary>Draws the crosshair guide lines (shadow + line) with a gap around the cursor.
    /// <paramref name="color"/> overrides the default white: Measure mode draws its reticle in
    /// measurement crimson, because there the reticle IS the tool.</summary>
    public static void DrawCrosshair(SD.Graphics g, SD.Size clientSize, SD.Point cursor, SD.Color? color = null)
    {
        var guides = FastSelectorGuideLayout.Calculate(clientSize, cursor, CrosshairGapPx);
        if (!guides.IsVisible)
            return;

        using var shadow = new SD.Pen(SD.Color.FromArgb(120, 0, 0, 0), 3);
        using var pen = new SD.Pen(color ?? SD.Color.FromArgb(210, 255, 255, 255), 1);
        DrawGuideLines(g, shadow, guides);
        DrawGuideLines(g, pen, guides);
    }

    private static void DrawGuideLines(SD.Graphics g, SD.Pen pen, FastSelectorGuideLines guides)
    {
        g.DrawLine(pen, guides.LeftStart, guides.LeftEnd);
        g.DrawLine(pen, guides.RightStart, guides.RightEnd);
        g.DrawLine(pen, guides.TopStart, guides.TopEnd);
        g.DrawLine(pen, guides.BottomStart, guides.BottomEnd);
    }

    /// <summary>Half-length of the T-serif at each end of a dimension line (PowerToys'
    /// FEET_HALF_LENGTH). The serifs are the whole reason the line reads as a measurement
    /// rather than as a guide.</summary>
    private const int SerifHalf = 2;

    /// <summary>Perpendicular gap between a dimension line and its pill. Internal because
    /// Measure mode places its own live pill by the same rule, and one constant in two files
    /// is one constant that drifts.</summary>
    internal const int DimensionPillGap = 6;

    /// <summary>
    /// A measured distance: an aliased 1 px hairline between two INCLUSIVE endpoints, a
    /// T-serif at each end, and its number in a fully-rounded pill on the segment's own
    /// midpoint — you read the value exactly where the gap is.
    ///
    /// The hairlines are filled rectangles rather than DrawLine calls. GDI+ decides for
    /// itself which endpoint of a line is covered, and that is precisely the off-by-one
    /// that separates a polished ruler from a sloppy one; a rectangle from a to b is
    /// b - a + 1 px long by construction, which is the same inclusive +1 EdgeSnap returns.
    ///
    /// <paramref name="serifLow"/> and <paramref name="serifHigh"/> drop the cap at the
    /// low (left / top) and high (right / bottom) end. That is how a soft edge is drawn:
    /// the serif is the claim "the object ends HERE", so an edge the detector barely found
    /// does not get to make it. The line stays an opaque aliased hairline either way —
    /// this vocabulary has no dashes, no glow and no alpha to fade.
    /// </summary>
    public static void DrawDimension(
        SD.Graphics g, SD.Size clientSize, SD.Point a, SD.Point b, string? label, SD.Color color,
        bool serifLow = true, bool serifHigh = true)
    {
        bool horizontal = a.Y == b.Y;
        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        using (var brush = new SD.SolidBrush(color))
        {
            if (horizontal)
            {
                int left = Math.Min(a.X, b.X);
                int right = Math.Max(a.X, b.X);
                g.FillRectangle(brush, left, a.Y, right - left + 1, 1);
                if (serifLow)
                    g.FillRectangle(brush, left, a.Y - SerifHalf, 1, (SerifHalf * 2) + 1);
                if (serifHigh)
                    g.FillRectangle(brush, right, a.Y - SerifHalf, 1, (SerifHalf * 2) + 1);
            }
            else
            {
                int top = Math.Min(a.Y, b.Y);
                int bottom = Math.Max(a.Y, b.Y);
                g.FillRectangle(brush, a.X, top, 1, bottom - top + 1);
                if (serifLow)
                    g.FillRectangle(brush, a.X - SerifHalf, top, (SerifHalf * 2) + 1, 1);
                if (serifHigh)
                    g.FillRectangle(brush, a.X - SerifHalf, bottom, (SerifHalf * 2) + 1, 1);
            }
        }
        g.SmoothingMode = prev;

        if (string.IsNullOrEmpty(label))
            return;

        SD.Size pill = MeasureLabel(label);
        SD.Point at;
        if (horizontal)
        {
            // Above the line, flipped below when the line is near the top of the surface —
            // the same flip the live pill does. Clamping instead would park the pill on top
            // of the very span it labels.
            int above = a.Y - pill.Height - DimensionPillGap;
            at = new SD.Point(((a.X + b.X) / 2) - (pill.Width / 2),
                above >= 0 ? above : a.Y + DimensionPillGap);
        }
        else
        {
            at = new SD.Point(a.X + DimensionPillGap, ((a.Y + b.Y) / 2) - (pill.Height / 2));
        }
        DrawLabel(g, clientSize, label, at.X, at.Y, fullyRounded: true);
    }

    /// <summary>A guide the user placed: one full-bleed hairline, no end caps, no pill. The
    /// ABSENCE of the dimension vocabulary is what says "a guide, not a distance".</summary>
    public static void DrawGuide(SD.Graphics g, SD.Size clientSize, bool vertical, int at, SD.Color color)
    {
        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        using (var brush = new SD.SolidBrush(color))
        {
            if (vertical)
                g.FillRectangle(brush, at, 0, 1, clientSize.Height);
            else
                g.FillRectangle(brush, 0, at, clientSize.Width, 1);
        }
        g.SmoothingMode = prev;
    }

    private const int LabelPadX = 16;
    private const int LabelPadY = 8;
    private const int LabelRadius = 6;

    private const WF.TextFormatFlags LabelText =
        WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.NoPadding;

    /// <summary>The rendered size of the label pill for <paramref name="text"/>, for callers
    /// that place the pill by an edge other than its top-left. A newline stacks another line —
    /// the measurement pill carries its dip value and ratio under the pixel figure.</summary>
    public static SD.Size MeasureLabel(string text)
    {
        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt, SD.FontStyle.Bold);
        SD.Size size = MeasureBlock(text, font);
        return new SD.Size(size.Width + LabelPadX, size.Height + LabelPadY);
    }

    private static SD.Size MeasureBlock(string text, SD.Font font)
    {
        if (!text.Contains('\n'))
            return WF.TextRenderer.MeasureText(text, font);

        int width = 0;
        int height = 0;
        foreach (string line in text.Split('\n'))
        {
            SD.Size measured = WF.TextRenderer.MeasureText(line.Length == 0 ? " " : line, font);
            width = Math.Max(width, measured.Width);
            height += measured.Height;
        }
        return new SD.Size(width, height);
    }

    /// <summary>The pill's ground opacity when it is fully faded in.</summary>
    private const int LabelGroundAlpha = 235;

    /// <summary>Draws the size/coordinate label pill, clamped inside the client area.
    /// <paramref name="fullyRounded"/> gives a capsule (radius = height/2), which is the
    /// measurement pill's shape; the existing selection readout keeps its radius-6 corners.
    /// <paramref name="amount"/> fades the whole pill in and out — GDI+ has no global alpha
    /// for a text draw, so the fade is applied to the two colours instead of to a layer.
    /// <paramref name="outline"/> strokes the capsule, which is how the measurement pill says
    /// it is a click target; pass the colour already carrying the hover fade's alpha.</summary>
    public static void DrawLabel(
        SD.Graphics g, SD.Size clientSize, string text, int x, int y, bool fullyRounded = false,
        double amount = 1.0, SD.Color? outline = null)
    {
        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt, SD.FontStyle.Bold);
        SD.Size size = MeasureBlock(text, font);
        int w = size.Width + LabelPadX;
        int h = size.Height + LabelPadY;
        int left = Math.Clamp(x, 0, Math.Max(0, clientSize.Width - w));
        int top = Math.Clamp(y, 0, Math.Max(0, clientSize.Height - h));
        var bg = new SD.Rectangle(left, top, w, h);

        double fade = Math.Clamp(amount, 0, 1);
        SD.Color text0 = SD.Color.FromArgb((int)Math.Round(255 * fade), ThemePalette.TextPrimary);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(bg, fullyRounded ? Math.Max(2, h / 2) : LabelRadius))
        {
            using (var bgBrush = new SD.SolidBrush(
                SD.Color.FromArgb((int)Math.Round(LabelGroundAlpha * fade), ThemePalette.WindowBg)))
                g.FillPath(bgBrush, path);
            if (outline is SD.Color stroke && stroke.A > 0)
            {
                using var pen = new SD.Pen(stroke, 1);
                g.DrawPath(pen, path);
            }
        }
        g.SmoothingMode = prev;

        if (!text.Contains('\n'))
        {
            WF.TextRenderer.DrawText(g, text, font, bg, text0, LabelText);
            return;
        }

        int lineTop = bg.Top + (LabelPadY / 2);
        foreach (string line in text.Split('\n'))
        {
            SD.Size measured = WF.TextRenderer.MeasureText(line.Length == 0 ? " " : line, font);
            WF.TextRenderer.DrawText(g, line, font,
                new SD.Rectangle(bg.Left, lineTop, bg.Width, measured.Height),
                text0, LabelText);
            lineTop += measured.Height;
        }
    }
}
