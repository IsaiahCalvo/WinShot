using System.Drawing.Drawing2D;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

/// <summary>
/// Geometry for the tall-capture hover preview. A scrolling capture is routinely 6,000 px
/// tall inside a ~150 px card, which centre-crops to an unreadable smudge; the preview is a
/// separate floating window that shows the whole strip at monitor height. Pure so the
/// gating and the placement can be checked without a screen.
/// </summary>
internal static class TallCapturePreviewLayout
{
    /// <summary>Long side must be at least this many times the short side before a card
    /// thumbnail stops being a usable review of the capture.</summary>
    public const double MinAspect = 3d;

    /// <summary>Below this the preview would be no more legible than the card itself.</summary>
    private const int MinLongSide = 400;

    public static bool ShouldOffer(SD.Size image)
    {
        if (image.Width <= 0 || image.Height <= 0) return false;
        int longSide = Math.Max(image.Width, image.Height);
        int shortSide = Math.Min(image.Width, image.Height);
        return longSide >= MinLongSide && longSide >= shortSide * MinAspect;
    }

    /// <summary>
    /// The preview's screen rect: as large as the working area allows along the capture's
    /// long axis, sitting clear of the card so the card stays clickable underneath.
    /// </summary>
    public static SD.Rectangle Place(SD.Size image, SD.Rectangle workingArea, SD.Rectangle cardScreen, int dpi)
    {
        double scale = Math.Clamp(dpi, 96, 480) / 96d;
        int gap = Math.Max(1, (int)Math.Round(12 * scale));

        // The band above the card is what a tall strip gets to use; a wide strip is limited
        // by the working area's width instead.
        int maxHeight = Math.Max(120, cardScreen.Top - workingArea.Top - (gap * 2));
        int maxWidth = Math.Max(120, workingArea.Width - (gap * 2));

        double aspect = (double)image.Width / image.Height;
        int height = maxHeight;
        int width = (int)Math.Round(height * aspect);
        if (width > maxWidth)
        {
            width = maxWidth;
            height = (int)Math.Round(width / aspect);
        }
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        // Right-aligned with the card and floating above it: the card lives in the
        // bottom-right corner, so this is the only direction with room, and the two read as
        // one stack rather than two unrelated windows.
        int x = cardScreen.Right - width;
        int y = cardScreen.Top - height - gap;
        return new SD.Rectangle(
            Math.Clamp(x, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - width)),
            Math.Clamp(y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - height)),
            width,
            height);
    }
}

/// <summary>
/// The hover preview itself: a floating, click-through, never-activating window showing the
/// whole capture. Additive on purpose - the quick-actions card's own chrome, geometry and
/// dismissal are untouched; this window only appears beside it.
/// </summary>
internal sealed class TallCapturePreviewWindow : WF.Form
{
    private const int CsDropShadow = 0x00020000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    // Click-through: the pointer must keep reaching whatever is underneath, and the preview
    // must never take a MouseEnter of its own - it exists only while the card is hovered.
    private const int WsExTransparent = 0x00000020;

    private readonly SD.Bitmap _scaled;
    private readonly int _cornerRadius;
    private readonly QuickAccessOverlayVisuals _visuals;
    private IDisposable? _fade;

    private TallCapturePreviewWindow(SD.Bitmap scaled, SD.Rectangle bounds, int dpi,
        QuickAccessOverlayVisuals visuals)
    {
        _scaled = scaled;
        _visuals = visuals;
        _cornerRadius = Math.Max(2, (int)Math.Round(8 * (Math.Clamp(dpi, 96, 480) / 96d)));

        AccessibleName = "Capture preview";
        AccessibleRole = WF.AccessibleRole.Graphic;
        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = visuals.CardFill;
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        Bounds = bounds;
        Opacity = 0d;
        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            WF.CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= CsDropShadow;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
            return parameters;
        }
    }

    /// <summary>
    /// Builds the preview for a capture whose aspect ratio earns one, or null when a card
    /// thumbnail is already a fair review of it. Takes a private copy of the pixels: the
    /// card's bitmap is edited in place by rotate/flip/resize.
    /// </summary>
    public static TallCapturePreviewWindow? TryCreate(SD.Bitmap image, SD.Rectangle cardScreen,
        int dpi, QuickAccessOverlayVisuals visuals)
    {
        if (image is null || !TallCapturePreviewLayout.ShouldOffer(image.Size))
            return null;

        SD.Rectangle workingArea = WF.Screen.FromRectangle(cardScreen).WorkingArea;
        SD.Rectangle bounds = TallCapturePreviewLayout.Place(image.Size, workingArea, cardScreen, dpi);
        if (bounds.Width < 2 || bounds.Height < 2)
            return null;

        // Scaled once here rather than per paint: a 32,000 px strip resampled on every
        // WM_PAINT would stutter the hover it is supposed to make useful.
        var scaled = new SD.Bitmap(bounds.Width, bounds.Height, SD.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var g = SD.Graphics.FromImage(scaled);
            // Bilinear, not bicubic: this runs on the UI thread the moment the pointer lands
            // on the card, and at these reduction ratios (a 6,000 px strip into ~900 px)
            // bicubic costs several times as much for no visible difference.
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // Same lock the card's background thumbnail render takes on this bitmap
            // (FastQuickActionsWindow.CreatePreviewBitmap). GDI+ locks an Image for the
            // duration of an operation, so two concurrent DrawImage calls on one source throw
            // "Object is currently in use elsewhere" -- and the loser is usually the
            // background render, which then leaves the card with an empty thumbnail for good.
            lock (image)
            {
                g.DrawImage(image, new SD.Rectangle(0, 0, bounds.Width, bounds.Height),
                    0, 0, image.Width, image.Height, SD.GraphicsUnit.Pixel);
            }
        }
        catch (Exception ex)
        {
            scaled.Dispose();
            Log.Error("Tall-capture preview could not be scaled (non-fatal)", ex);
            return null;
        }

        return new TallCapturePreviewWindow(scaled, bounds, dpi, visuals);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_visuals.CardFill);
        var surface = new SD.Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var path = GdiPaths.RoundedRect(surface, _cornerRadius);
        using (var brush = new SD.TextureBrush(_scaled))
            g.FillPath(brush, path);
        using var edge = new SD.Pen(_visuals.CardEdge, 1);
        g.DrawPath(edge, path);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        using var path = GdiPaths.RoundedRect(new SD.Rectangle(SD.Point.Empty, ClientSize), _cornerRadius);
        Region?.Dispose();
        Region = new SD.Region(path);
        _fade?.Dispose();
        _fade = Motion.Run(Motion.QuickMs, Motion.EaseOutCubic, SetOpacity);
    }

    /// <summary>Fades out and closes itself; safe to call more than once.</summary>
    public void FadeOutAndClose()
    {
        if (IsDisposed) return;
        _fade?.Dispose();
        _fade = null;
        if (!IsHandleCreated || !Visible)
        {
            Close();
            Dispose();
            return;
        }

        double from = Opacity;
        _fade = Motion.Run(Motion.QuickMs, Motion.EaseInCubic,
            p => SetOpacity(from * (1d - p)),
            () => { if (!IsDisposed) { Close(); Dispose(); } });
    }

    private void SetOpacity(double value)
    {
        if (IsDisposed) return;
        Opacity = Math.Clamp(value, 0d, 1d);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fade?.Dispose();
            _fade = null;
            _scaled.Dispose();
        }
        base.Dispose(disposing);
    }
}
