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
    public static void DrawCrosshair(SD.Graphics g, SD.Size clientSize, SD.Point cursor)
    {
        var guides = FastSelectorGuideLayout.Calculate(clientSize, cursor, CrosshairGapPx);
        if (!guides.IsVisible)
            return;

        using var shadow = new SD.Pen(SD.Color.FromArgb(120, 0, 0, 0), 3);
        using var pen = new SD.Pen(SD.Color.FromArgb(210, 255, 255, 255), 1);
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

    /// <summary>The rendered size of the label pill for <paramref name="text"/>, for callers
    /// that place the pill by an edge other than its top-left.</summary>
    public static SD.Size MeasureLabel(string text)
    {
        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt, SD.FontStyle.Bold);
        using SD.Graphics measuring = SD.Graphics.FromHwnd(IntPtr.Zero);
        return PillSize(measuring, font, text);
    }

    /// <summary>The pill's rect for a string, measured through the renderer that fills it.
    /// It used to be sized with TextRenderer (GDI) and drawn with DrawString (GDI+), which
    /// disagree in both directions -- GDI+ is narrower for the size readouts and wider for
    /// the instruction strings, so the 8 px side padding this adds collapsed to about 3 px on
    /// the longest one and one more word would have pushed the text past the pill.</summary>
    private static SD.Size PillSize(SD.Graphics g, SD.Font font, string text)
    {
        SD.SizeF size = g.MeasureString(text, font, int.MaxValue, LabelFormat);
        return new SD.Size((int)Math.Ceiling(size.Width) + 16, (int)Math.Ceiling(size.Height) + 8);
    }

    /// <summary>Centred, and NoWrap so a long instruction overflows visibly rather than
    /// silently wrapping inside a pill sized for one line. Shared by the measure and the
    /// draw, because the two agreeing is the whole point.</summary>
    private static readonly SD.StringFormat LabelFormat = new()
    {
        Alignment = SD.StringAlignment.Center,
        LineAlignment = SD.StringAlignment.Center,
        FormatFlags = SD.StringFormatFlags.NoWrap,
    };

    /// <summary>Draws the size/coordinate label pill, clamped inside the client area.
    /// <paramref name="alpha"/> scales the whole pill, so a caller can fade one out
    /// (TextRenderer cannot draw translucent text, so the text goes through
    /// Graphics.DrawString at every alpha -- switching renderers mid-fade moved the glyphs).</summary>
    public static void DrawLabel(SD.Graphics g, SD.Size clientSize, string text, int x, int y,
        double alpha = 1d)
    {
        double amount = Math.Clamp(alpha, 0d, 1d);
        if (amount <= 0d) return;

        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt, SD.FontStyle.Bold);
        SD.Size pill = PillSize(g, font, text);
        int w = pill.Width;
        int h = pill.Height;
        int left = Math.Clamp(x, 0, Math.Max(0, clientSize.Width - w));
        int top = Math.Clamp(y, 0, Math.Max(0, clientSize.Height - h));
        var bg = new SD.Rectangle(left, top, w, h);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(bg, 6))
        using (var bgBrush = new SD.SolidBrush(SD.Color.FromArgb(Scale(235, amount), ThemePalette.WindowBg)))
            g.FillPath(bgBrush, path);
        g.SmoothingMode = prev;

        // One renderer at every alpha. It used to draw through TextRenderer (GDI, hinted) at
        // rest and through DrawString (GDI+, different metrics) below 1, so the pill's text
        // visibly re-laid out on the first frame of the 140 ms fade -- a snap inside the
        // transition that exists so nothing snaps.
        using var textBrush = new SD.SolidBrush(SD.Color.FromArgb(Scale(255, amount), ThemePalette.TextPrimary));
        g.DrawString(text, font, textBrush, bg, LabelFormat);
    }

    private static int Scale(int value, double amount)
        => (int)Math.Clamp(Math.Round(value * amount), 0d, 255d);
}
