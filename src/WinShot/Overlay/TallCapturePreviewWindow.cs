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
    /// <summary>Below this the band above the card cannot hold anything worth showing.
    /// Logical pixels: scaled with the DPI like every other metric here, or the gate passed
    /// on 80 logical px of room at 150 % and 60 at 200 % -- a preview no more readable than
    /// the card it replaces, which is the opposite of what the gate is for.</summary>
    private const int MinBandLogical = 120;

    /// <summary>A preview may take at most this much of the working area's width. Without it
    /// a 3.2:1 window grab flies out across nearly the whole screen on a casual hover.</summary>
    private const double MaxWidthFraction = 2d / 3d;

    public static SD.Rectangle Place(SD.Size image, SD.Rectangle workingArea, SD.Rectangle cardScreen, int dpi)
    {
        double scale = Math.Clamp(dpi, 96, 480) / 96d;
        int gap = Math.Max(1, (int)Math.Round(12 * scale));

        // The band above the card is what the preview gets to use, and it is a hard ceiling:
        // a floor here let the preview grow back down over the card it is meant to sit above.
        // Too little room and there is no preview at all.
        int minBand = Math.Max(1, (int)Math.Round(MinBandLogical * scale));
        int maxHeight = cardScreen.Top - workingArea.Top - (gap * 2);
        if (maxHeight < minBand)
            return SD.Rectangle.Empty;
        int maxWidth = Math.Max(minBand, (int)(workingArea.Width * MaxWidthFraction));

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
    private bool _ownsScaled;
    private readonly int _cornerRadius;
    private readonly QuickAccessOverlayVisuals _visuals;
    private IDisposable? _fade;

    private TallCapturePreviewWindow(SD.Bitmap scaled, bool ownsScaled, SD.Rectangle bounds, int dpi,
        QuickAccessOverlayVisuals visuals)
    {
        _scaled = scaled;
        _ownsScaled = ownsScaled;
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
    /// Where a preview of this capture would go, or null when its aspect ratio does not earn
    /// one or the card leaves no room above it. Cheap -- no pixels are touched -- so the card
    /// can ask on every hover before deciding whether to pay for a resample.
    /// </summary>
    public static SD.Rectangle? PlaceFor(SD.Bitmap image, SD.Rectangle cardScreen, int dpi)
    {
        if (image is null)
            return null;

        // Through the lock, like every other read of a card's live bitmap: the card asks for a
        // placement on the same tick a pool thread can be inside Scale's DrawImage on it, and a
        // bare Image.Width there throws "Object is currently in use elsewhere".
        SD.Size size = CaptureService.GetBitmapSize(image);
        if (!TallCapturePreviewLayout.ShouldOffer(size))
            return null;

        SD.Rectangle workingArea = WF.Screen.FromRectangle(cardScreen).WorkingArea;
        SD.Rectangle bounds = TallCapturePreviewLayout.Place(size, workingArea, cardScreen, dpi);
        return bounds.Width < 2 || bounds.Height < 2 ? null : bounds;
    }

    /// <summary>
    /// The expensive half: the whole capture resampled down to the preview's size. Safe to
    /// call off the UI thread, and meant to be -- the gate only fires on the largest images
    /// the app produces (up to 32,000 px), and paying for that inline on MouseEnter froze the
    /// card's own hover cross-fade behind it.
    /// </summary>
    public static SD.Bitmap? Scale(SD.Bitmap image, SD.Size size)
    {
        if (image is null || size.Width < 2 || size.Height < 2)
            return null;

        var scaled = new SD.Bitmap(size.Width, size.Height, SD.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var g = SD.Graphics.FromImage(scaled);
            // Bilinear, not bicubic: at these reduction ratios (a 6,000 px strip into ~900 px)
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
                g.DrawImage(image, new SD.Rectangle(0, 0, size.Width, size.Height),
                    0, 0, image.Width, image.Height, SD.GraphicsUnit.Pixel);
            }
        }
        catch (Exception ex)
        {
            scaled.Dispose();
            Log.Error("Tall-capture preview could not be scaled (non-fatal)", ex);
            return null;
        }

        return scaled;
    }

    /// <summary>Wraps an already-scaled bitmap in a window. The caller keeps the bitmap so it
    /// can be reused across hovers; only <see cref="TryCreate"/> hands over ownership.</summary>
    public static TallCapturePreviewWindow Create(SD.Bitmap scaled, SD.Rectangle bounds, int dpi,
        QuickAccessOverlayVisuals visuals)
        => new(scaled, ownsScaled: false, bounds, dpi, visuals);

    /// <summary>
    /// Builds the preview inline for a capture whose aspect ratio earns one, or null when a
    /// card thumbnail is already a fair review of it. The window owns the scaled copy.
    /// </summary>
    public static TallCapturePreviewWindow? TryCreate(SD.Bitmap image, SD.Rectangle cardScreen,
        int dpi, QuickAccessOverlayVisuals visuals)
    {
        if (PlaceFor(image, cardScreen, dpi) is not SD.Rectangle bounds)
            return null;

        SD.Bitmap? scaled = Scale(image, bounds.Size);
        return scaled is null ? null : new TallCapturePreviewWindow(scaled, true, bounds, dpi, visuals);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_visuals.CardFill);
        // The fill uses the same rect the region does (OnShown), so no raw background shows
        // through on the right and bottom.
        using (var path = GdiPaths.RoundedRect(new SD.Rectangle(SD.Point.Empty, ClientSize), _cornerRadius))
        using (var brush = new SD.TextureBrush(_scaled))
            g.FillPath(brush, path);

        // FastQuickActionsWindow.DrawCardEdge's recipe, verbatim: a hairline laid ON the
        // fill's own boundary picks up more ink on one side than the other (measured 96 vs 69
        // on the card before it was fixed). Inset by one with PixelOffsetMode.Half, every side
        // sits on solid fill and reads the same.
        GraphicsState state = g.Save();
        try
        {
            g.TranslateTransform(1, 1);
            g.PixelOffsetMode = PixelOffsetMode.Half;
            PopupChrome.DrawBorder(
                g,
                new SD.Size(Math.Max(1, ClientSize.Width - 2), Math.Max(1, ClientSize.Height - 2)),
                Math.Max(1, _cornerRadius - 1),
                _visuals.CardEdge);
        }
        finally
        {
            g.Restore(state);
        }
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

    /// <summary>
    /// Brings a preview that is mid fade-out back up. A pointer that leaves the card and
    /// returns inside the 140 ms exit used to build a second window over the dying one, and
    /// two identically placed TopMost windows read as a flicker.
    /// </summary>
    public void FadeIn()
    {
        if (IsDisposed) return;
        _fade?.Dispose();
        double from = Opacity;
        _fade = Motion.Run(Motion.QuickMs, Motion.EaseOutCubic, p => SetOpacity(from + ((1d - from) * p)));
    }

    /// <summary>
    /// Fades out and closes, taking the scaled bitmap with it. The card calls this when what
    /// the flyout is showing has gone stale -- the pixels were edited, or the card has started
    /// moving out from under it. The outgoing strip keeps painting itself through its 140 ms
    /// exit while the card builds the replacement, instead of having to be cut down in one
    /// frame so the card can dispose the bitmap underneath it.
    /// </summary>
    public void FadeOutAndCloseOwningBitmap()
    {
        _ownsScaled = true;
        FadeOutAndClose();
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
            if (_ownsScaled)
                _scaled.Dispose();
        }
        base.Dispose(disposing);
    }
}
