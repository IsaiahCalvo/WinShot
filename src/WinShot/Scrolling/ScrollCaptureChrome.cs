using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Scrolling;

/// <summary>
/// Marks a window so it stays visible on screen but is skipped by screen capture
/// (WGC / Desktop Duplication). Essential for the scrolling chrome: the dim overlay and
/// controls must NOT end up in the frames the capture loop stitches.
/// </summary>
internal static class CaptureExclusion
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public static void Apply(IntPtr hwnd)
    {
        try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
        catch { /* pre-2004 Windows: no exclusion available; non-fatal */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}

/// <summary>
/// Full-monitor dim with the capture region punched out as a bright, accent-framed hole,
/// so during a scrolling capture the user sees exactly what's being captured while still
/// scrolling the live content underneath. Click-through (WS_EX_TRANSPARENT) so all input
/// passes to the content, and excluded from capture so it never taints the stitched frames.
/// </summary>
public sealed class ScrollDimOverlay : WF.Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    private readonly SD.Rectangle _monitor;
    private readonly SD.Rectangle _holeLocal;

    public ScrollDimOverlay(SD.Rectangle regionScreen)
    {
        _monitor = WF.Screen.FromRectangle(regionScreen).Bounds;
        var hole = SD.Rectangle.Intersect(regionScreen, _monitor);
        _holeLocal = new SD.Rectangle(hole.X - _monitor.X, hole.Y - _monitor.Y, hole.Width, hole.Height);

        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        Bounds = _monitor;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExclusion.Apply(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try { RenderLayered(); }
        catch (Exception ex) { Log.Error("Scroll dim overlay render failed (non-fatal)", ex); }
    }

    private void RenderLayered()
    {
        int w = Math.Max(1, _monitor.Width), h = Math.Max(1, _monitor.Height);
        using var bmp = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;
            using (var dim = new SD.SolidBrush(SD.Color.FromArgb(110, 0, 0, 0)))
                g.FillRectangle(dim, 0, 0, w, h);

            if (_holeLocal.Width > 0 && _holeLocal.Height > 0)
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SD.SolidBrush(SD.Color.FromArgb(0, 0, 0, 0)))
                    g.FillRectangle(clear, _holeLocal);

                g.CompositingMode = CompositingMode.SourceOver;
                using var pen = new SD.Pen(SD.Color.FromArgb(255, ThemePalette.Accent), 2f);
                g.DrawRectangle(pen, _holeLocal.X - 2, _holeLocal.Y - 2, _holeLocal.Width + 3, _holeLocal.Height + 3);
            }
        }
        PushLayered(bmp);
    }

    private void PushLayered(SD.Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(SD.Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = _monitor.X, y = _monitor.Y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
}

/// <summary>
/// Small Cancel / Done bar shown next to the capture region (replaces the old "Stop" pill).
/// Placed just below the region; flips above when the region runs to the bottom of the
/// screen, and tucks inside the bottom of the region when it spans the full screen height.
/// Never activates (wheel keeps reaching the content) and is excluded from capture.
/// Done keeps the stitched result; Cancel discards it.
/// </summary>
public sealed class ScrollControlsBar : WF.Form
{
    public event Action? CancelRequested;
    public event Action? DoneRequested;
    public event Action? RecoverRequested;

    private readonly WF.Label _status;
    private readonly WF.Button _recover;
    private readonly SD.Rectangle _region;
    private bool _tooFast; // a "scroll slower" warning is showing; it overrides live status text

    // Amber warning — the palette only has red (error); "slow down" is a nudge, not an error.
    private static readonly SD.Color WarnColor = SD.Color.FromArgb(0xFF, 0x9F, 0x0A);

    public ScrollControlsBar(SD.Rectangle regionScreen)
    {
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        BackColor = ThemePalette.Elevated;
        Padding = new WF.Padding(12, 8, 12, 8);

        // Fixed width so the live status text ("Captured 12 frames - 4000px") never grows the
        // bar mid-capture (which would otherwise leave the rounded region stale and clip Done).
        _status = new WF.Label
        {
            AutoSize = false,
            Size = new SD.Size(230, 24),
            AutoEllipsis = true,
            ForeColor = ThemePalette.TextSecondary,
            Font = ThemePalette.UiFont(9f),
            Text = "Scrolling capture…",
            TextAlign = SD.ContentAlignment.MiddleLeft,
            Anchor = WF.AnchorStyles.None,
        };

        // Shown only while a section is skipped: WinShot scrolls back and re-captures it.
        _recover = MakeButton("Recover", WarnColor, SD.Color.White, SD.Color.FromArgb(0xFF, 0xB0, 0x7A, 0x20));
        _recover.Visible = false;
        _recover.Click += (_, _) => RecoverRequested?.Invoke();

        var cancel = MakeButton("Cancel", ThemePalette.SurfaceAlt, ThemePalette.TextPrimary, ThemePalette.SurfaceHover);
        cancel.Click += (_, _) => CancelRequested?.Invoke();

        var done = MakeButton("Done", ThemePalette.Accent, SD.Color.White, ThemePalette.AccentHover);
        done.Click += (_, _) => DoneRequested?.Invoke();

        var table = new WF.TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            Dock = WF.DockStyle.Fill,
            BackColor = SD.Color.Transparent,
        };
        table.Controls.Add(_status, 0, 0);
        table.Controls.Add(_recover, 1, 0);
        table.Controls.Add(cancel, 2, 0);
        table.Controls.Add(done, 3, 0);

        Controls.Add(table);
        AutoSize = true;
        AutoSizeMode = WF.AutoSizeMode.GrowAndShrink;

        _region = regionScreen;
        Shown += (_, _) =>
        {
            ApplyRoundedRegion(Width, Height, 12);
            PositionNear(_region);
        };
        // Re-round AND re-center when the bar resizes (e.g. the Recover button appears/disappears),
        // so it never clips the content or drifts off-center.
        SizeChanged += (_, _) => { ApplyRoundedRegion(Width, Height, 12); PositionNear(_region); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExclusion.Apply(Handle);
    }

    public void SetStatus(string text)
    {
        if (IsDisposed || _tooFast) return; // the warning takes precedence over live status
        _status.ForeColor = ThemePalette.TextSecondary;
        _status.Text = text;
    }

    /// <summary>Shows/clears an amber "scroll slower" warning that overrides the live status text
    /// until cleared (the next <see cref="SetStatus"/> repaints normal text once cleared).</summary>
    public void SetTooFast(bool on)
    {
        if (IsDisposed) return;
        _tooFast = on;
        _recover.Visible = on; // offer "Recover" exactly while a section is skipped
        if (on)
        {
            _status.ForeColor = WarnColor;
            _status.Text = "⚠ Section skipped — scroll back, or hit Recover";
        }
    }

    private static WF.Button MakeButton(string text, SD.Color back, SD.Color fore, SD.Color hover)
    {
        var b = new WF.Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            FlatStyle = WF.FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Padding = new WF.Padding(14, 6, 14, 6),
            Margin = new WF.Padding(8, 0, 0, 0),
            Cursor = WF.Cursors.Hand,
            Anchor = WF.AnchorStyles.None,
            Font = ThemePalette.UiFont(9.5f, SD.FontStyle.Bold),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = hover;
        return b;
    }

    private void PositionNear(SD.Rectangle region)
    {
        var mon = WF.Screen.FromRectangle(region).Bounds;
        const int gap = 14, pad = 8;
        int x = region.Left + (region.Width - Width) / 2;

        int below = region.Bottom + gap;
        int above = region.Top - Height - gap;
        int y;
        if (below + Height <= mon.Bottom - pad)
            y = below;                                  // room below the region
        else if (above >= mon.Top + pad)
            y = above;                                  // else above the region
        else
            y = region.Bottom - Height - gap;           // full-height region: tuck inside the bottom

        x = Math.Clamp(x, mon.Left + pad, Math.Max(mon.Left + pad, mon.Right - Width - pad));
        y = Math.Clamp(y, mon.Top + pad, Math.Max(mon.Top + pad, mon.Bottom - Height - pad));
        Location = new SD.Point(x, y);
    }

    private void ApplyRoundedRegion(int w, int h, int r)
    {
        if (w < r * 2 || h < r * 2) return;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, r, r, 180, 90);
        path.AddArc(w - r, 0, r, r, 270, 90);
        path.AddArc(w - r, h - r, r, r, 0, 90);
        path.AddArc(0, h - r, r, r, 90, 90);
        path.CloseFigure();
        Region = new SD.Region(path);
    }
}

/// <summary>
/// Live preview panel pinned to the bottom-right of the region's monitor, showing the
/// stitched image growing as the capture proceeds (CleanShot shows the same). Excluded from
/// capture so it never appears in the stitch; never activates.
/// </summary>
public sealed class ScrollPreviewPanel : WF.Form
{
    private readonly WF.PictureBox _pic;
    private readonly WF.Label _caption;

    public ScrollPreviewPanel(SD.Rectangle regionScreen)
    {
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        BackColor = ThemePalette.Elevated;
        Padding = new WF.Padding(8);
        Size = new SD.Size(220, 300);

        _caption = new WF.Label
        {
            Dock = WF.DockStyle.Bottom,
            Height = 20,
            ForeColor = ThemePalette.TextSecondary,
            Font = ThemePalette.UiFont(8.5f),
            Text = "Capturing…",
            TextAlign = SD.ContentAlignment.MiddleCenter,
        };
        _pic = new WF.PictureBox
        {
            Dock = WF.DockStyle.Fill,
            SizeMode = WF.PictureBoxSizeMode.Zoom,
            BackColor = ThemePalette.WindowBg,
        };
        Controls.Add(_pic);
        Controls.Add(_caption);

        Shown += (_, _) => PositionBottomRight(regionScreen);
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExclusion.Apply(Handle);
    }

    /// <summary>Takes ownership of <paramref name="image"/> (disposes the previous one).</summary>
    public void SetImage(SD.Bitmap image, string caption)
    {
        if (IsDisposed) { image.Dispose(); return; }
        var old = _pic.Image;
        _pic.Image = image;
        old?.Dispose();
        _caption.Text = caption;
    }

    protected override void OnFormClosed(WF.FormClosedEventArgs e)
    {
        _pic.Image?.Dispose();
        _pic.Image = null;
        base.OnFormClosed(e);
    }

    private void PositionBottomRight(SD.Rectangle region)
    {
        var mon = WF.Screen.FromRectangle(region).Bounds;
        Location = new SD.Point(mon.Right - Width - 20, mon.Bottom - Height - 20);
    }
}
