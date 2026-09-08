using System.Drawing.Drawing2D;
using System.IO;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.History;

public sealed class FastQuickPreviewWindow : WF.Form
{
    private static readonly string[] PreviewableExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private static readonly SD.Color TextColor = ThemePalette.TextPrimary;
    private static readonly SD.Color MutedText = ThemePalette.TextSecondary;

    private readonly string _filePath;
    private readonly bool _looksLikeImage;
    /// <summary>The creation monitor's scale. <see cref="WF.AutoScaleMode.Dpi"/> never fires
    /// for the monitor a window is born on, so every logical measurement here is scaled by
    /// hand like every other WinForms surface in the app.</summary>
    private readonly double _scale;
    private SD.Image? _image;
    private bool _closing;
    private bool _exiting;
    private IDisposable? _motion;
    private double _enterAmount;

    private int S(int logical) => Recording.RecordingMonitorDpi.Px(logical, _scale);

    public FastQuickPreviewWindow(string filePath)
    {
        _filePath = filePath;
        _scale = Recording.RecordingMonitorDpi.ScaleFor(
            WF.Screen.FromPoint(WF.Cursor.Position).Bounds);
        _looksLikeImage = PreviewableExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());
        _image = _looksLikeImage ? TryLoadImage(filePath) : null;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        ClientSize = CalculateSize();
        Opacity = 0;    // OnShown fades it up; see the entrance note there.
        KeyDown += (_, e) =>
        {
            if (e.KeyCode is WF.Keys.Space or WF.Keys.Escape)
                CloseOnce();
        };
        MouseDown += (_, _) => CloseOnce();
        Deactivate += (_, _) => CloseOnce();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateWindowRegion();
        CenterOnScreen();
        // Quick Look was the last surface in the app that blinked. It reads worst on a
        // repeated gesture: History rebuilds the window on every Left/Right step, so arrowing
        // through ten captures was ten hard cuts. Same recipe as the toast —
        // EaseOutCubic in and EaseInCubic back out.
        _motion?.Dispose();
        _motion = Motion.Run(Motion.EnterDurationMs, Motion.EaseOutCubic, ApplyEntrance);
    }

    /// <summary>0 = fully out (transparent), 1 = at rest. Opacity only: the window is
    /// centred, so a rise would read as drift rather than as an entrance.</summary>
    private void ApplyEntrance(double amount)
    {
        if (IsDisposed || !IsHandleCreated) return;
        _enterAmount = Math.Clamp(amount, 0d, 1d);
        Opacity = _enterAmount;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnFormClosing(WF.FormClosingEventArgs e)
    {
        _closing = true;
        // Entrance and exit are the same move in reverse. Windows shutting down is the
        // exception the toast documents: cancelling a close whose message loop will not run
        // again strands the window.
        bool ordinaryClose =
            e.CloseReason is WF.CloseReason.None or WF.CloseReason.UserClosing or WF.CloseReason.FormOwnerClosing;
        if (!_exiting && ordinaryClose && IsHandleCreated && !IsDisposed && Motion.AnimationsEnabled())
        {
            e.Cancel = true;
            _exiting = true;
            _motion?.Dispose();
            double from = _enterAmount;
            _motion = Motion.Run(
                Motion.SwapMs, Motion.EaseInCubic, p => ApplyEntrance(from * (1d - p)), Close);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // A caller that disposes mid-tween never runs OnClosed, and an orphaned tween
            // holds the process-wide timer resolution for good.
            _motion?.Dispose();
            _motion = null;
            _image?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Back);

        int inset = S(8);
        var content = new SD.Rectangle(inset, inset, Width - inset * 2, Height - inset * 2);
        if (_image is not null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_image, content);
        }
        else
        {
            DrawFallback(e.Graphics, content);
        }

        PopupChrome.DrawBorder(e.Graphics, ClientSize, S(14));
    }

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW, matching the Quick Access card
            return cp;
        }
    }

    private SD.Size CalculateSize()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;

        if (_image is not null)
        {
            // The image is already in physical pixels — a screenshot is not resized by DPI —
            // so only the chrome around it scales.
            return HistoryPreviewLayout.CalculateImageSize(
                new SD.Size(_image.Width, _image.Height),
                area.Size,
                S(8) * 2);
        }

        SD.Size fallback = HistoryPreviewLayout.FallbackSize;
        return new SD.Size(S(fallback.Width), S(fallback.Height));
    }

    private void DrawFallback(SD.Graphics g, SD.Rectangle content)
    {
        string fileName = string.IsNullOrWhiteSpace(_filePath) ? "Preview" : Path.GetFileName(_filePath);
        string message = _looksLikeImage ? "Preview unavailable" : "Press Open to play";
        // Pixel-unit fonts, not point: a GraphicsUnit.Point font drawn with TextRenderer
        // resolves against a different monitor's DPI than the one this card is on, which is
        // exactly the mismatch ThemePalette.UiFontPx exists to avoid.
        using var titleFont = ThemePalette.UiFontPx(ThemePalette.PxForPt(ThemePalette.TextTitlePt, _scale));
        // Two steps apart on purpose: the filename is the heading, the status line is not.
        using var messageFont = ThemePalette.UiFontPx(ThemePalette.PxForPt(ThemePalette.TextBodyLgPt, _scale));
        var titleRect = new SD.Rectangle(content.X + S(20), content.Y + S(22), content.Width - S(40), S(28));
        var messageRect = new SD.Rectangle(content.X + S(20), content.Y + S(56), content.Width - S(40), S(24));
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.EndEllipsis;
        WF.TextRenderer.DrawText(g, fileName, titleFont, titleRect, TextColor, flags);
        WF.TextRenderer.DrawText(g, message, messageFont, messageRect, MutedText, flags);
    }

    private void CenterOnScreen()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        Location = new SD.Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        PopupChrome.ApplyRegion(this, S(14));
    }

    private void CloseOnce()
    {
        if (_closing)
            return;

        _closing = true;
        Close();
    }

    /// <summary>
    /// Closes in this call, skipping the exit fade. The fade cancels the first close and
    /// finishes it 220 ms later, so <see cref="WF.Form.Close"/> returns with the window still
    /// on screen, TopMost and hit-testable. That is correct for a dismissal the user asked
    /// for, and wrong for a REPLACEMENT: History rebuilds this window on every Left/Right
    /// step, and the outgoing preview would frame the incoming one and swallow the next
    /// click. A caller about to show a successor uses this.
    /// </summary>
    public void CloseNow()
    {
        _exiting = true;
        Close();
    }

    private static SD.Image? TryLoadImage(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var loaded = SD.Image.FromStream(stream);
            return new SD.Bitmap(loaded);
        }
        catch (Exception ex)
        {
            Log.Error($"Fast quick preview failed for {path}", ex);
            return null;
        }
    }

}
