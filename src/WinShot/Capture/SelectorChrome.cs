using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Shared visual chrome for the capture selectors (<see cref="FastRegionSelectorDialog"/>
/// and <see cref="FastAllInOneSelectorDialog"/>): the crosshair guide lines, the
/// size/coordinate label pill, and the common overlay-surface setup. Kept in one place so
/// both selectors draw identical chrome and a styling change happens once.
/// </summary>
internal static class SelectorChrome
{
    private const int CrosshairGapPx = 10;
    private const double LiveDesktopOpacity = 0.45;

    /// <summary>Logical padding around a pill's text, and its corner radius.</summary>
    private const int PillPadX = 16;
    private const int PillPadY = 8;
    private const int PillRadius = 6;

    /// <summary>Gap between a labelled rectangle and the size pill parked off its corner.</summary>
    private const int PillGap = 8;

    private static double Clean(double scale) => scale is > 0d and < 100d ? scale : 1d;

    private static int Px(int logical, double scale) =>
        WinShot.Recording.RecordingMonitorDpi.Px(logical, Clean(scale));

    /// <summary>
    /// The chrome font, in PIXELS for the monitor at <paramref name="scale"/>.
    ///
    /// The pills measure with <c>TextRenderer.MeasureText</c> (GDI) and draw with
    /// <c>Graphics.DrawString</c> (GDI+, the only one of the two that honours alpha — see
    /// <see cref="DrawCenteredText"/>). A GraphicsUnit.Point font resolves that point size
    /// against the screen DC captured when the Font was built — the primary monitor — on the
    /// GDI side, and against the target Graphics' own DpiY on the GDI+ side. Under
    /// PerMonitorV2 those are two different monitors, so every pill on a display whose DPI
    /// differs from the primary's was measured at one text size and painted at another:
    /// clipped when the painting monitor was the coarser, padded out when it was the finer.
    /// Pixels have no such ambiguity, which is exactly what
    /// <c>FastRegionSelectorDialog.ActionBarFont</c> already does for the Cancel/Done bar.
    /// </summary>
    private static SD.Font PillFont(double scale) =>
        ThemePalette.UiFontPx(ThemePalette.PxForPt(ThemePalette.TextBodyPt, Clean(scale)), SD.FontStyle.Bold);

    /// <summary>How far down-and-right of the pointer a pill that FOLLOWS the pointer sits,
    /// on a monitor at <paramref name="scale"/>. One relationship, one pair of numbers:
    /// window mode places its size readout with these too, and the user switches between the
    /// two surfaces with Space.</summary>
    public static SD.Size ReadoutOffset(double scale) =>
        new(Px(CursorReadoutOffsetX, scale), Px(CursorReadoutOffsetY, scale));

    /// <summary>
    /// Centred text that actually honours the alpha in <paramref name="color"/>.
    ///
    /// <c>TextRenderer.DrawText</c> is a GDI (DrawTextW) wrapper and its colour goes through
    /// ColorTranslator.ToWin32, which is <c>R | G&lt;&lt;8 | B&lt;&lt;16</c> — the alpha byte is
    /// dropped on the floor. Every fading label drawn that way rendered fully opaque for the
    /// whole tween and then vanished, and the shutter pill's cross-fade printed both of its
    /// labels solid on top of each other for the entire 140 ms. GDI+ <c>DrawString</c> does
    /// honour alpha, which is why the quick-actions card's flash line already uses it.
    /// </summary>
    public static void DrawCenteredText(
        SD.Graphics g, string text, SD.Font font, SD.Rectangle bounds, SD.Color color)
    {
        if (color.A <= 0)
            return;
        // Typographic metrics: no GDI+ overhang padding, so the string stays inside a pill
        // whose width came from TextRenderer.MeasureText.
        using var format = new SD.StringFormat(SD.StringFormat.GenericTypographic)
        {
            Alignment = SD.StringAlignment.Center,
            LineAlignment = SD.StringAlignment.Center,
            FormatFlags = SD.StringFormatFlags.NoWrap,
        };
        var prevHint = g.TextRenderingHint;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using (var brush = new SD.SolidBrush(color))
            g.DrawString(text, font, brush, bounds, format);
        g.TextRenderingHint = prevHint;
    }

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

    /// <summary>Draws the crosshair guide lines (shadow + white) with a gap around the cursor.</summary>
    public static void DrawCrosshair(SD.Graphics g, SD.Size clientSize, SD.Point cursor, double scale)
    {
        var guides = FastSelectorGuideLayout.Calculate(clientSize, cursor, Px(CrosshairGapPx, scale));
        if (!guides.IsVisible)
            return;

        // The guide lines grow with the monitor like the gap between them already did.
        // With crosshair mode defaulting to "always" these are the most-seen chrome in the
        // app, and at 150 % a 1 px white line is two-thirds of a logical pixel -- a hairline
        // that half-disappears into the dim.
        float weight = (float)Math.Max(1d, Clean(scale));
        using var shadow = new SD.Pen(SD.Color.FromArgb(120, 0, 0, 0), 3f * weight);
        using var pen = new SD.Pen(SD.Color.FromArgb(210, 255, 255, 255), weight);
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

    /// <summary>
    /// The cursor readout pill, offset down-and-right of the pointer and flipped back inside
    /// when that would run off the surface. This is the readout the crosshair carries when the
    /// magnifier is off — before this the coordinates lived only inside the loupe, so turning
    /// the magnifier off (or tapping Alt) left the selector with no position readout at all.
    /// </summary>
    public static void DrawCursorReadout(
        SD.Graphics g, SD.Size clientSize, string text, SD.Point cursor, double scale)
    {
        SD.Size pill = MeasureLabel(text, scale);
        SD.Size offset = ReadoutOffset(scale);
        int left = cursor.X + offset.Width;
        int top = cursor.Y + offset.Height;
        if (left + pill.Width > clientSize.Width)
            left = cursor.X - offset.Width - pill.Width;
        if (top + pill.Height > clientSize.Height)
            top = cursor.Y - offset.Height - pill.Height;
        DrawLabel(g, clientSize, text, left, top, scale);
    }

    /// <summary>The pointer-follow offset in LOGICAL px. Callers want
    /// <see cref="ReadoutOffset"/>, which scales it for the monitor it lands on.</summary>
    private const int CursorReadoutOffsetX = 16;
    private const int CursorReadoutOffsetY = 20;

    /// <summary>
    /// Where the size pill for a detected element sits, in the same space as the rectangle it
    /// labels, CLAMPED inside <paramref name="bounds"/> the way <see cref="DrawLabel"/> clamps
    /// it inside the client area. Paint and hit-test both call this, so the pill you can click
    /// is exactly the pill you can see — with the clamp only on the paint side, an element
    /// flush with the right or bottom edge of a monitor painted its pill pulled inward while
    /// the hit rect stayed off screen, so the shutter was dead in exactly that corner.
    /// </summary>
    public static SD.Rectangle SizePillRect(
        SD.Rectangle labelled, SD.Size pill, SD.Rectangle bounds, double scale)
    {
        int gap = Px(PillGap, scale);
        int left = Math.Clamp(labelled.Right + gap, bounds.Left, Math.Max(bounds.Left, bounds.Right - pill.Width));
        int top = Math.Clamp(labelled.Bottom + gap, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - pill.Height));
        return new SD.Rectangle(left, top, pill.Width, pill.Height);
    }

    /// <summary>
    /// The size pill cross-fading into a camera glyph as the pointer moves onto it. Same
    /// geometry throughout — the pill is not a new control, it is the existing readout
    /// admitting that clicking it takes the shot.
    ///
    /// The pill ground is painted ONCE at full alpha and only the two foregrounds cross-fade.
    /// Fading a whole label out under a whole label fading in composed two 235-alpha grounds
    /// down to about 180 at the midpoint, so the chrome visibly thinned over whatever was
    /// behind it for the ~70 ms it was changing — the one moment it is being looked at.
    /// </summary>
    public static void DrawShutterPill(
        SD.Graphics g, SD.Size clientSize, string text, SD.Point at, double swap, double scale)
    {
        double amount = Math.Clamp(swap, 0d, 1d);
        SD.Size pill = MeasureLabel(text, scale);
        int left = Math.Clamp(at.X, 0, Math.Max(0, clientSize.Width - pill.Width));
        int top = Math.Clamp(at.Y, 0, Math.Max(0, clientSize.Height - pill.Height));
        var bounds = new SD.Rectangle(left, top, pill.Width, pill.Height);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(bounds, Px(PillRadius, scale)))
        using (var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(235, ThemePalette.WindowBg)))
            g.FillPath(bgBrush, path);
        g.SmoothingMode = prev;

        if (amount < 1d)
        {
            using var font = PillFont(scale);
            DrawCenteredText(g, text, font, bounds,
                SD.Color.FromArgb((int)Math.Round(255 * (1d - amount)), ThemePalette.TextPrimary));
        }

        if (amount > 0d)
        {
            using var glyphFont = ThemePalette.IconFontPx(
                ThemePalette.PxForPt(ThemePalette.GlyphBadgePt, scale));
            DrawCenteredText(g, "", glyphFont, bounds,
                SD.Color.FromArgb((int)Math.Round(255 * amount), ThemePalette.TextPrimary));
        }
    }

    /// <summary>Where the hint/message pill sits by default: centred, one eighth down.</summary>
    public static SD.Rectangle HintPillRect(SD.Size clientSize, string text, double scale)
    {
        SD.Size pill = MeasureLabel(text, scale);
        return new SD.Rectangle(
            (clientSize.Width - pill.Width) / 2, Math.Max(0, clientSize.Height / 8),
            pill.Width, pill.Height);
    }

    /// <summary>Draws the first-run hint pill centred near the top of a monitor, or at
    /// <paramref name="top"/> when the caller has to dodge something (the action bar).</summary>
    public static void DrawHint(
        SD.Graphics g, SD.Size clientSize, string text, double opacity, double scale, int? top = null)
    {
        SD.Rectangle at = HintPillRect(clientSize, text, scale);
        DrawLabel(g, clientSize, text, at.X, top ?? at.Y, scale, opacity);
    }

    /// <summary>The rendered size of the label pill for <paramref name="text"/>, for callers
    /// that place the pill by an edge other than its top-left.</summary>
    public static SD.Size MeasureLabel(string text, double scale)
    {
        using var font = PillFont(scale);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        return new SD.Size(size.Width + Px(PillPadX, scale), size.Height + Px(PillPadY, scale));
    }

    /// <summary>Draws the size/coordinate label pill, clamped inside the client area.</summary>
    public static void DrawLabel(SD.Graphics g, SD.Size clientSize, string text, int x, int y, double scale) =>
        DrawLabel(g, clientSize, text, x, y, scale, 1d);

    /// <summary>
    /// The label pill at a given opacity, for chrome that fades in and out. Both the pill and
    /// its text carry the alpha, so a fading pill never leaves ghost text behind.
    /// </summary>
    public static void DrawLabel(
        SD.Graphics g, SD.Size clientSize, string text, int x, int y, double scale, double opacity)
    {
        int alpha = (int)Math.Round(255 * Math.Clamp(opacity, 0d, 1d));
        if (alpha <= 0)
            return;
        using var font = PillFont(scale);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        int w = size.Width + Px(PillPadX, scale);
        int h = size.Height + Px(PillPadY, scale);
        int left = Math.Clamp(x, 0, Math.Max(0, clientSize.Width - w));
        int top = Math.Clamp(y, 0, Math.Max(0, clientSize.Height - h));
        var bg = new SD.Rectangle(left, top, w, h);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(bg, Px(PillRadius, scale)))
        using (var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(alpha * 235 / 255, ThemePalette.WindowBg)))
            g.FillPath(bgBrush, path);
        g.SmoothingMode = prev;

        DrawCenteredText(g, text, font, bg, SD.Color.FromArgb(alpha, ThemePalette.TextPrimary));
    }
}
