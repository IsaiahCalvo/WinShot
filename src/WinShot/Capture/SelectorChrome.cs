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

    /// <summary>Draws the crosshair guide lines (shadow + line) with a gap around the cursor.
    /// <paramref name="color"/> overrides the default white: Measure mode draws its reticle in
    /// measurement crimson, because there the reticle IS the tool.
    ///
    /// A colour override also buys the heavier halo, for the reason
    /// <see cref="DrawHairlineShadow"/> spells out: 120 alpha is enough for white (1.05
    /// luminance-wise it clears 3:1 against anything a desktop can show) and nowhere near
    /// enough for crimson, which at 120 composites its ground to #4A4A4A and reads 1.91:1 —
    /// under WCAG 1.4.11's 3:1 floor. The reticle is this mode's primary affordance and was
    /// the one measurement element drawing without the halo the rest of the vocabulary
    /// carries.</summary>
    public static void DrawCrosshair(
        SD.Graphics g, SD.Size clientSize, SD.Point cursor, double scale, SD.Color? color = null)
    {
        var guides = FastSelectorGuideLayout.Calculate(clientSize, cursor, Px(CrosshairGapPx, scale));
        if (!guides.IsVisible)
            return;

        // The guide lines grow with the monitor like the gap between them already did.
        // With crosshair mode defaulting to "always" these are the most-seen chrome in the
        // app, and at 150 % a 1 px white line is two-thirds of a logical pixel -- a hairline
        // that half-disappears into the dim.
        float weight = (float)Math.Max(1d, Clean(scale));
        using var shadow = new SD.Pen(
            SD.Color.FromArgb(color is null ? 120 : HairlineShadowAlpha, 0, 0, 0), 3f * weight);
        using var pen = new SD.Pen(color ?? SD.Color.FromArgb(210, 255, 255, 255), weight);
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

    /// <summary>Half-length of the T-serif at each end of a dimension line (PowerToys'
    /// FEET_HALF_LENGTH). The serifs are the whole reason the line reads as a measurement
    /// rather than as a guide. Deliberately NOT scaled: a ruler's own marks are pixel-exact,
    /// because the pixel is the unit being reported.</summary>
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
    /// <paramref name="serifs"/> drops both caps, which is the one place the two vocabularies
    /// meet: a pair of guides the user placed labels its own gap, and §3.1 allows that pill
    /// but not the caps — crimson claims an edge the app found, blue never does.
    /// </summary>
    public static void DrawDimension(
        SD.Graphics g, SD.Size clientSize, SD.Point a, SD.Point b, string? label, SD.Color color,
        double scale, bool serifs = true)
    {
        bool horizontal = a.Y == b.Y;
        var parts = new List<SD.Rectangle>(3);
        if (horizontal)
        {
            int left = Math.Min(a.X, b.X);
            int right = Math.Max(a.X, b.X);
            parts.Add(new SD.Rectangle(left, a.Y, right - left + 1, 1));
            if (serifs)
            {
                parts.Add(new SD.Rectangle(left, a.Y - SerifHalf, 1, (SerifHalf * 2) + 1));
                parts.Add(new SD.Rectangle(right, a.Y - SerifHalf, 1, (SerifHalf * 2) + 1));
            }
        }
        else
        {
            int top = Math.Min(a.Y, b.Y);
            int bottom = Math.Max(a.Y, b.Y);
            parts.Add(new SD.Rectangle(a.X, top, 1, bottom - top + 1));
            if (serifs)
            {
                parts.Add(new SD.Rectangle(a.X - SerifHalf, top, (SerifHalf * 2) + 1, 1));
                parts.Add(new SD.Rectangle(a.X - SerifHalf, bottom, (SerifHalf * 2) + 1, 1));
            }
        }

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        DrawHairlineShadow(g, parts);
        using (var brush = new SD.SolidBrush(color))
        {
            foreach (SD.Rectangle part in parts)
                g.FillRectangle(brush, part);
        }
        g.SmoothingMode = prev;

        if (string.IsNullOrEmpty(label))
            return;

        SD.Size pill = MeasureLabel(label, scale);
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
            // Right of the line, flipped left when there is no room — the mirror of the
            // horizontal flip above. Letting DrawLabel clamp instead would slide the pill
            // back across the very hairline it labels and hide it under its own ground.
            int right = a.X + DimensionPillGap;
            int midY = ((a.Y + b.Y) / 2) - (pill.Height / 2);
            at = right + pill.Width <= clientSize.Width
                ? new SD.Point(right, midY)
                : new SD.Point(a.X - DimensionPillGap - pill.Width, midY);
        }
        // The pill wears its own line's colour as a 1 px outline. Crimson and blue are only
        // 1.27:1 apart in luminance and the number inside is always TextPrimary on WindowBg,
        // so without this the thing the eye actually reads — the figure — carries none of the
        // grammar and a guide-pair gap is indistinguishable from a held span.
        DrawLabel(g, clientSize, label, at.X, at.Y, scale, 1d, fullyRounded: true, outline: color);
    }

    /// <summary>A guide the user placed: one full-bleed hairline, no end caps, no pill. The
    /// ABSENCE of the dimension vocabulary is what says "a guide, not a distance".</summary>
    public static void DrawGuide(SD.Graphics g, SD.Size clientSize, bool vertical, int at, SD.Color color)
    {
        SD.Rectangle line = vertical
            ? new SD.Rectangle(at, 0, 1, clientSize.Height)
            : new SD.Rectangle(0, at, clientSize.Width, 1);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        DrawHairlineShadow(g, new[] { line });
        using (var brush = new SD.SolidBrush(color))
            g.FillRectangle(brush, line);
        g.SmoothingMode = prev;
    }

    /// <summary>
    /// The 1 px halo every measurement hairline sits on. A coloured 1 px line drawn straight
    /// onto arbitrary desktop pixels can land on a surface of its own colour and vanish —
    /// crimson #E0244A over a mid-red button is 1.17:1, well under WCAG 1.4.11's 3:1 floor
    /// for a graphical object, and no amount of dimming helps because the measured run is
    /// BRIGHTENED, not dimmed. So the ruler carries its own contrast, one pixel proud on
    /// every side.
    ///
    /// The alpha is 215 rather than the crosshair's 120 because 120 does not actually buy
    /// the 3:1: over white it composites to #878787, which is 1.29:1 against crimson, and
    /// there is a whole band of mid-tone backdrops where crimson clears neither the halo nor
    /// the ground. 215 composites the brightest possible backdrop down to #2C (L = 0.024),
    /// which is 3.03:1 — unconditional, on any desktop pixel.
    ///
    /// Filled as one winding path rather than as separate rectangles, so the serif/span
    /// overlaps do not double the alpha into a dark knot at each end of the dimension.
    /// </summary>
    private static void DrawHairlineShadow(SD.Graphics g, IReadOnlyList<SD.Rectangle> parts)
    {
        using var path = new SD.Drawing2D.GraphicsPath { FillMode = SD.Drawing2D.FillMode.Winding };
        foreach (SD.Rectangle part in parts)
        {
            SD.Rectangle halo = part;
            halo.Inflate(1, 1);
            path.AddRectangle(halo);
        }
        using var shadow = new SD.SolidBrush(SD.Color.FromArgb(HairlineShadowAlpha, 0, 0, 0));
        g.FillPath(shadow, path);
    }

    /// <summary>The alpha the paragraph above derives. Shared with the Measure reticle, which
    /// is a measurement element and needs the same floor.</summary>
    private const int HairlineShadowAlpha = 215;

    /// <summary>The rendered size of the label pill for <paramref name="text"/>, for callers
    /// that place the pill by an edge other than its top-left. A newline stacks another line —
    /// the measurement pill carries its dip value and ratio under the pixel figure.</summary>
    public static SD.Size MeasureLabel(string text, double scale)
    {
        using var font = PillFont(scale);
        SD.Size size = MeasureBlock(text, font);
        return new SD.Size(size.Width + Px(PillPadX, scale), size.Height + Px(PillPadY, scale));
    }

    /// <summary>One measured block, stacking on newlines so a multi-line pill is sized by its
    /// widest line and the sum of its line heights.</summary>
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

    /// <summary>Draws the size/coordinate label pill, clamped inside the client area.</summary>
    public static void DrawLabel(SD.Graphics g, SD.Size clientSize, string text, int x, int y, double scale) =>
        DrawLabel(g, clientSize, text, x, y, scale, 1d);

    /// <summary>
    /// The label pill at a given opacity, for chrome that fades in and out. Both the pill and
    /// its text carry the alpha, so a fading pill never leaves ghost text behind.
    ///
    /// <paramref name="fullyRounded"/> gives a capsule (radius = height/2), which is the
    /// measurement pill's shape; the existing selection readout keeps its radius-6 corners.
    /// <paramref name="outline"/> strokes the capsule, which is how the measurement pill says
    /// it is a click target; pass the colour already carrying the hover fade's alpha. A
    /// newline stacks another line, so the measurement pill can carry its dip value and ratio
    /// under the pixel figure.
    /// </summary>
    public static void DrawLabel(
        SD.Graphics g, SD.Size clientSize, string text, int x, int y, double scale, double opacity,
        bool fullyRounded = false, SD.Color? outline = null)
    {
        int alpha = (int)Math.Round(255 * Math.Clamp(opacity, 0d, 1d));
        if (alpha <= 0)
            return;
        using var font = PillFont(scale);
        SD.Size size = MeasureBlock(text, font);
        int w = size.Width + Px(PillPadX, scale);
        int h = size.Height + Px(PillPadY, scale);
        int left = Math.Clamp(x, 0, Math.Max(0, clientSize.Width - w));
        int top = Math.Clamp(y, 0, Math.Max(0, clientSize.Height - h));
        var bg = new SD.Rectangle(left, top, w, h);
        var textColor = SD.Color.FromArgb(alpha, ThemePalette.TextPrimary);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(
            bg, fullyRounded ? Math.Max(2, h / 2) : Px(PillRadius, scale)))
        {
            using (var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(alpha * 235 / 255, ThemePalette.WindowBg)))
                g.FillPath(bgBrush, path);
            if (outline is SD.Color stroke && stroke.A > 0)
            {
                using var pen = new SD.Pen(SD.Color.FromArgb(stroke.A * alpha / 255, stroke), 1);
                g.DrawPath(pen, path);
            }
        }
        g.SmoothingMode = prev;

        if (!text.Contains('\n'))
        {
            DrawCenteredText(g, text, font, bg, textColor);
            return;
        }

        int lineTop = bg.Top + (Px(PillPadY, scale) / 2);
        foreach (string line in text.Split('\n'))
        {
            SD.Size measured = WF.TextRenderer.MeasureText(line.Length == 0 ? " " : line, font);
            DrawCenteredText(g, line, font,
                new SD.Rectangle(bg.Left, lineTop, bg.Width, measured.Height), textColor);
            lineTop += measured.Height;
        }
    }
}
