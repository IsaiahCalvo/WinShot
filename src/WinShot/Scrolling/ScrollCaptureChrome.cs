using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Scrolling;

/// <summary>
/// Full-monitor dim with the capture region punched out as a bright, accent-framed hole,
/// so during a scrolling capture the user sees exactly what's being captured while still
/// scrolling the live content underneath. Purely cosmetic and click-through
/// (WS_EX_TRANSPARENT) — all mouse/wheel input passes through to the content below.
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
                // Punch a fully transparent hole so the live content shows through bright.
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SD.SolidBrush(SD.Color.FromArgb(0, 0, 0, 0)))
                    g.FillRectangle(clear, _holeLocal);

                // Crisp accent frame drawn ENTIRELY outside the hole, so the 2px stroke never
                // bleeds into the captured region (it would otherwise show up in the stitch).
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
        IntPtr hBmp = bmp.GetHbitmap(SD.Color.FromArgb(0)); // 32bpp w/ alpha for a PArgb source
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
/// Small Cancel / Done bar shown next to the capture region during a scrolling capture
/// (replaces the old top-center "Stop" pill). Never activates, so wheel input keeps going
/// to the content being scrolled. Done keeps the stitched result; Cancel discards it.
/// </summary>
public sealed class ScrollControlsBar : WF.Form
{
    public event Action? CancelRequested;
    public event Action? DoneRequested;

    private readonly WF.Label _status;

    public ScrollControlsBar(SD.Rectangle regionScreen)
    {
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        BackColor = ThemePalette.Elevated;
        Padding = new WF.Padding(12, 8, 12, 8);

        _status = new WF.Label
        {
            AutoSize = true,
            ForeColor = ThemePalette.TextSecondary,
            Font = ThemePalette.UiFont(9f),
            Text = "Scrolling capture…",
            Anchor = WF.AnchorStyles.None,
        };

        var cancel = MakeButton("Cancel", ThemePalette.SurfaceAlt, ThemePalette.TextPrimary, ThemePalette.SurfaceHover);
        cancel.Click += (_, _) => CancelRequested?.Invoke();

        var done = MakeButton("Done", ThemePalette.Accent, SD.Color.White, ThemePalette.AccentHover);
        done.Click += (_, _) => DoneRequested?.Invoke();

        var table = new WF.TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Dock = WF.DockStyle.Fill,
            BackColor = SD.Color.Transparent,
        };
        table.Controls.Add(_status, 0, 0);
        table.Controls.Add(cancel, 1, 0);
        table.Controls.Add(done, 2, 0);

        Controls.Add(table);
        AutoSize = true;
        AutoSizeMode = WF.AutoSizeMode.GrowAndShrink;

        Shown += (_, _) =>
        {
            ApplyRoundedRegion();
            PositionNear(regionScreen);
        };
    }

    protected override bool ShowWithoutActivation => true;

    public void SetStatus(string text)
    {
        if (!IsDisposed)
            _status.Text = text;
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

    private void PositionNear(SD.Rectangle regionScreen)
    {
        var mon = WF.Screen.FromRectangle(regionScreen).Bounds;
        int x = regionScreen.Left + (regionScreen.Width - Width) / 2;
        int y = regionScreen.Bottom + 14;                  // prefer just below the region
        if (y + Height > mon.Bottom - 8)
            y = regionScreen.Top - Height - 14;            // flip above if there's no room
        x = Math.Clamp(x, mon.Left + 8, Math.Max(mon.Left + 8, mon.Right - Width - 8));
        y = Math.Clamp(y, mon.Top + 8, Math.Max(mon.Top + 8, mon.Bottom - Height - 8));
        Location = new SD.Point(x, y);
    }

    private void ApplyRoundedRegion()
    {
        int r = 12, w = Width, h = Height;
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
