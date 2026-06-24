using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Pin;

public sealed class FastPinWindow : WF.Form
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const int WmNclbuttondown = 0x00A1;
    private const int WmNchittest = 0x0084;
    private const int WmSizing = 0x0214;
    private static readonly IntPtr HtCaption = new(2);

    // WM_NCHITTEST results for the resize border / interior.
    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    // WM_SIZING wParam edge codes.
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;

    private const int ResizeBorder = 6;          // px of outer edge that resizes the pin
    private const int ToolbarButtonSize = 26;
    private const int ToolbarButtonGap = 4;
    private const int ToolbarPad = 6;
    private const int ReadoutDurationMs = 800;

    private static readonly SD.Font ToolbarGlyphFont = ThemePalette.IconFont(10f);
    private static readonly SD.Font LockBadgeFont = ThemePalette.IconFont(9f);

    private static readonly List<FastPinWindow> OpenPins = new();
    private static int _openCount;

    private readonly SD.Bitmap _image;
    private readonly SettingsService? _settings;
    private readonly int _naturalWidth;
    private readonly int _naturalHeight;
    private readonly WF.ContextMenuStrip _menu;
    private readonly WF.ToolStripMenuItem _lockItem;
    private readonly List<ToolbarButton> _toolbarButtons = new();
    private readonly WF.Timer _readoutTimer = new() { Interval = ReadoutDurationMs };
    private double _scale;
    private bool _locked;
    private double _opacityBeforeLock = 1.0;
    private bool _mouseInside;
    private int _hoverButton = -1;
    private string? _readoutText;
    private SD.Point _readoutPoint;

    public FastPinWindow(SD.Bitmap image, SettingsService? settings = null)
    {
        _image = image;
        _settings = settings;
        _naturalWidth = image.Width;
        _naturalHeight = image.Height;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = SD.Color.Black;
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

        _menu = new WF.ContextMenuStrip();
        _menu.Items.Add("Copy", null, async (_, _) => await CopyAsync());
        _menu.Items.Add("Save...", null, async (_, _) => await SaveAsync());
        _lockItem = new WF.ToolStripMenuItem("Lock (Ctrl+L)", null, (_, _) => SetLocked(!_locked));
        _menu.Items.Add(_lockItem);
        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add("Close", null, (_, _) => Close());
        ContextMenuStrip = _menu;

        // The hover toolbar reuses the exact actions the context menu already exposes.
        _toolbarButtons.Add(new ToolbarButton("", "Copy", () => _ = CopyAsync()));
        _toolbarButtons.Add(new ToolbarButton("", "Save", () => _ = SaveAsync()));
        _toolbarButtons.Add(new ToolbarButton("", "Lock", () => SetLocked(!_locked)));
        _toolbarButtons.Add(new ToolbarButton("", "Close", Close));

        var area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        _scale = Math.Min(1.0, Math.Min(area.Width * 0.6 / _naturalWidth, area.Height * 0.6 / _naturalHeight));
        ApplyScale();

        double offset = (_openCount++ % 8) * 24;
        Location = new SD.Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2) + (int)offset,
            area.Top + Math.Max(0, (area.Height - Height) / 2) + (int)offset);

        MouseEnter += (_, _) => { _mouseInside = true; Invalidate(); };
        MouseLeave += (_, _) => { _mouseInside = false; SetHoverButton(-1); Invalidate(); };
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left && HitTestButton(e.Location) < 0)
                Close();
        };
        MouseWheel += OnPinMouseWheel;
        KeyDown += OnPinKeyDown;
        _readoutTimer.Tick += (_, _) =>
        {
            _readoutTimer.Stop();
            _readoutText = null;
            Invalidate();
        };
        Closed += (_, _) =>
        {
            _readoutTimer.Dispose();
            OpenPins.Remove(this);
            _image.Dispose();
            MemoryCleanup.Request();
        };
        OpenPins.Add(this);
    }

    public static void UnlockAllPins()
    {
        foreach (var pin in OpenPins.ToList())
            pin.SetLocked(false);
    }

    public static void Prewarm(SettingsService? settings = null)
    {
        try
        {
            using var bitmap = new SD.Bitmap(1, 1);
            using var pin = new FastPinWindow((SD.Bitmap)bitmap.Clone(), settings)
            {
                Opacity = 0,
                ShowInTaskbar = false,
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-32000, -32000),
            };
            pin.Show();
            WF.Application.DoEvents();
            pin.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Fast pin prewarm failed", ex);
        }
    }

    public static void TrackFirstShown(WF.Form form, string metricName)
    {
        var sw = Stopwatch.StartNew();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null)
                form.Shown -= handler;
            Log.Info($"Perf {metricName} first show: {sw.ElapsedMilliseconds} ms");
        };
        form.Shown += handler;
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(_image, new SD.Rectangle(1, 1, Math.Max(1, ClientSize.Width - 2), Math.Max(1, ClientSize.Height - 2)));

        if (_mouseInside && !_locked)
        {
            using var pen = new SD.Pen(ThemePalette.Accent, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            DrawToolbar(e.Graphics);
        }

        if (_locked)
            DrawLockBadge(e.Graphics);

        if (_readoutText is not null)
            DrawReadout(e.Graphics, _readoutText, _readoutPoint);

        base.OnPaint(e);
    }

    // ----- Hover toolbar -----------------------------------------------------

    private SD.Rectangle ToolbarBounds()
    {
        int count = _toolbarButtons.Count;
        int rowWidth = count * ToolbarButtonSize + (count - 1) * ToolbarButtonGap;
        int width = rowWidth + ToolbarPad * 2;
        int height = ToolbarButtonSize + ToolbarPad * 2;
        int x = Math.Max(1, (ClientSize.Width - width) / 2);
        int y = 4;
        return new SD.Rectangle(x, y, width, height);
    }

    private SD.Rectangle ButtonBounds(int index)
    {
        SD.Rectangle bar = ToolbarBounds();
        int x = bar.Left + ToolbarPad + index * (ToolbarButtonSize + ToolbarButtonGap);
        int y = bar.Top + ToolbarPad;
        return new SD.Rectangle(x, y, ToolbarButtonSize, ToolbarButtonSize);
    }

    private void DrawToolbar(SD.Graphics g)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        SD.Rectangle bar = ToolbarBounds();

        using (var bg = new SD.SolidBrush(SD.Color.FromArgb(235, ThemePalette.ToolbarBg)))
        using (var border = new SD.Pen(ThemePalette.Border))
        {
            FillRoundedRect(g, bg, bar, 8);
            DrawRoundedRect(g, border, bar, 8);
        }

        for (int i = 0; i < _toolbarButtons.Count; i++)
            DrawButton(g, ButtonBounds(i), GlyphFor(i), i == _hoverButton);
    }

    private string GlyphFor(int index)
    {
        // The lock toggle reflects current state: closed padlock (E72E) when locked, open (E785) when unlocked.
        if (string.Equals(_toolbarButtons[index].Tip, "Lock", StringComparison.Ordinal))
            return _locked ? "" : "";
        return _toolbarButtons[index].Glyph;
    }

    private static void DrawButton(SD.Graphics g, SD.Rectangle bounds, string glyph, bool hot)
    {
        // Mirrors FastQuickActionsWindow.DrawButton: rest = dim glyph, hover = HoverFill circle.
        if (hot)
        {
            using var hover = new SD.SolidBrush(ThemePalette.HoverFill);
            g.FillEllipse(hover, bounds);
        }

        SD.Color glyphColor = hot ? ThemePalette.TextPrimary : ThemePalette.TextSecondary;
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, glyph, ToolbarGlyphFont, bounds, glyphColor, flags);
    }

    private void DrawLockBadge(SD.Graphics g)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        var badge = new SD.Rectangle(6, 6, 22, 22);
        using var bg = new SD.SolidBrush(SD.Color.FromArgb(217, ThemePalette.ToolbarBg));
        using var border = new SD.Pen(ThemePalette.Accent);
        g.FillEllipse(bg, badge);
        g.DrawEllipse(border, badge);

        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, "", LockBadgeFont, badge, ThemePalette.TextPrimary, flags);
    }

    private void DrawReadout(SD.Graphics g, string text, SD.Point near)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using var font = ThemePalette.UiFont(9f);
        SD.Size size = WF.TextRenderer.MeasureText(text, font);
        int w = size.Width + 16;
        int h = size.Height + 8;
        int x = Math.Clamp(near.X + 12, 2, Math.Max(2, ClientSize.Width - w - 2));
        int y = Math.Clamp(near.Y + 12, 2, Math.Max(2, ClientSize.Height - h - 2));
        var pill = new SD.Rectangle(x, y, w, h);

        using var bg = new SD.SolidBrush(SD.Color.FromArgb(235, ThemePalette.ToolbarBg));
        using var border = new SD.Pen(ThemePalette.Border);
        FillRoundedRect(g, bg, pill, h / 2);
        DrawRoundedRect(g, border, pill, h / 2);

        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, text, font, pill, ThemePalette.TextPrimary, flags);
    }

    private void ShowReadout(string text, SD.Point at)
    {
        _readoutText = text;
        _readoutPoint = at;
        _readoutTimer.Stop();
        _readoutTimer.Start();
        Invalidate();
    }

    private int HitTestButton(SD.Point point)
    {
        if (!_mouseInside || _locked)
            return -1;
        for (int i = 0; i < _toolbarButtons.Count; i++)
        {
            if (ButtonBounds(i).Contains(point))
                return i;
        }
        return -1;
    }

    private void SetHoverButton(int index)
    {
        if (_hoverButton == index)
            return;
        _hoverButton = index;
        Invalidate(ToolbarBounds());
    }

    // ----- Scale / layout ----------------------------------------------------

    private void ApplyScale()
    {
        ClientSize = new SD.Size(
            Math.Max(1, (int)Math.Round(_naturalWidth * _scale) + 2),
            Math.Max(1, (int)Math.Round(_naturalHeight * _scale) + 2));
    }

    private void SyncScaleFromClientSize()
    {
        // Resize via the border edges drives ClientSize; derive _scale back from it so
        // wheel-resize and keyboard nudges keep working against the new size.
        double sx = (ClientSize.Width - 2) / (double)_naturalWidth;
        double sy = (ClientSize.Height - 2) / (double)_naturalHeight;
        _scale = Math.Clamp(Math.Max(sx, sy), PinInteraction.MinScale, PinInteraction.MaxScale);
    }

    // ----- Mouse -------------------------------------------------------------

    private void OnMouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left)
            return;

        // A click on a toolbar button must not start a window drag; it fires on MouseUp.
        if (HitTestButton(e.Location) >= 0)
            return;

        ReleaseCapture();
        SendMessage(Handle, WmNclbuttondown, HtCaption, IntPtr.Zero);
    }

    private void OnMouseMove(object? sender, WF.MouseEventArgs e)
    {
        SetHoverButton(HitTestButton(e.Location));
    }

    private void OnMouseUp(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left)
            return;

        int index = HitTestButton(e.Location);
        if (index >= 0)
            _toolbarButtons[index].Action();
    }

    private void OnPinMouseWheel(object? sender, WF.MouseEventArgs e)
    {
        if ((ModifierKeys & WF.Keys.Control) == WF.Keys.Control)
        {
            Opacity = PinInteraction.AdjustOpacity(Opacity, e.Delta);
            ShowReadout($"{(int)Math.Round(Opacity * 100)}%", PointToClient(WF.Cursor.Position));
            return;
        }

        double newScale = PinInteraction.AdjustScale(_scale, e.Delta);
        if (Math.Abs(newScale - _scale) < 0.0001)
            return;

        double factor = newScale / _scale;
        Left -= (int)Math.Round(e.X * (factor - 1));
        Top -= (int)Math.Round(e.Y * (factor - 1));
        _scale = newScale;
        ApplyScale();
        ShowReadout($"{(int)Math.Round(_scale * 100)}%", PointToClient(WF.Cursor.Position));
        Invalidate();
    }

    private void OnPinKeyDown(object? sender, WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape)
        {
            Close();
            return;
        }

        if (e.KeyCode == WF.Keys.L && e.Control)
        {
            SetLocked(!_locked);
            e.Handled = true;
            return;
        }

        // Reset scale (and opacity) to 100% — keeps double-click reserved for close.
        if (e.KeyCode == WF.Keys.D0 && e.Control)
        {
            ResetToNatural();
            e.Handled = true;
            return;
        }

        int step = PinInteraction.NudgeStep(e.Shift);
        switch (e.KeyCode)
        {
            case WF.Keys.Left: Left -= step; e.Handled = true; break;
            case WF.Keys.Right: Left += step; e.Handled = true; break;
            case WF.Keys.Up: Top -= step; e.Handled = true; break;
            case WF.Keys.Down: Top += step; e.Handled = true; break;
        }
    }

    private void ResetToNatural()
    {
        Opacity = 1.0;
        _opacityBeforeLock = 1.0;
        _scale = 1.0;
        ApplyScale();
        ShowReadout("100%", PointToClient(WF.Cursor.Position));
        Invalidate();
    }

    private void SetLocked(bool locked)
    {
        if (_locked == locked)
            return;

        long style = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        if (locked)
        {
            _opacityBeforeLock = Opacity;
            SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(style | WsExTransparent));
            Opacity = PinInteraction.LockedOpacity(_opacityBeforeLock);
            _mouseInside = false;
            SetHoverButton(-1);
        }
        else
        {
            SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(style & ~WsExTransparent));
            Opacity = _opacityBeforeLock;
        }

        _locked = locked;
        _lockItem.Text = locked ? "Unlock (Ctrl+L)" : "Lock (Ctrl+L)";
        Invalidate();
    }

    // ----- Resize border + aspect ratio --------------------------------------

    protected override void WndProc(ref WF.Message m)
    {
        // A locked pin is WS_EX_TRANSPARENT, so it never receives these messages anyway;
        // the tray "Unlock pinned windows" command (UnlockAllPins) is the escape hatch.
        if (m.Msg == WmNchittest && !_locked)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)HtClient)
            {
                int hit = HitTestResizeBorder();
                if (hit != HtClient)
                    m.Result = (IntPtr)hit;
            }
            return;
        }

        if (m.Msg == WmSizing && !_locked)
        {
            base.WndProc(ref m);
            ConstrainAspectRatio(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    private int HitTestResizeBorder()
    {
        SD.Point p = PointToClient(WF.Cursor.Position);
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        bool left = p.X <= ResizeBorder;
        bool right = p.X >= w - ResizeBorder;
        bool top = p.Y <= ResizeBorder;
        bool bottom = p.Y >= h - ResizeBorder;

        // Don't steal the top edge from the hover toolbar buttons.
        if (top && HitTestButton(p) >= 0)
            return HtClient;

        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return HtClient;
    }

    private void ConstrainAspectRatio(ref WF.Message m)
    {
        var rc = Marshal.PtrToStructure<Rect>(m.LParam);
        int edge = m.WParam.ToInt32();

        // Non-client chrome is zero here (borderless form), but keep the +2 image margin.
        double aspect = _naturalWidth / (double)_naturalHeight;
        int newWidth = rc.Right - rc.Left;
        int newHeight = rc.Bottom - rc.Top;

        // Floor matches PinInteraction.MinScale so a drag can't shrink the pin to nothing.
        int minImgW = Math.Max(16, (int)Math.Round(_naturalWidth * PinInteraction.MinScale));
        int minImgH = Math.Max(16, (int)Math.Round(_naturalHeight * PinInteraction.MinScale));
        int imgW = Math.Max(minImgW, newWidth - 2);
        int imgH = Math.Max(minImgH, newHeight - 2);

        bool horizontalDrag = edge is WmszLeft or WmszRight;
        bool verticalDrag = edge is WmszTop or WmszBottom;

        int targetImgW;
        int targetImgH;
        if (horizontalDrag)
        {
            targetImgW = imgW;
            targetImgH = (int)Math.Round(targetImgW / aspect);
        }
        else if (verticalDrag)
        {
            targetImgH = imgH;
            targetImgW = (int)Math.Round(targetImgH * aspect);
        }
        else
        {
            // Corner drag: let width lead, derive height.
            targetImgW = imgW;
            targetImgH = (int)Math.Round(targetImgW / aspect);
        }

        int targetW = targetImgW + 2;
        int targetH = targetImgH + 2;

        // Apply the constrained size against the anchored edge so the opposite side stays put.
        switch (edge)
        {
            case WmszLeft:
            case WmszTopLeft:
            case WmszBottomLeft:
                rc.Left = rc.Right - targetW;
                break;
            default:
                rc.Right = rc.Left + targetW;
                break;
        }

        switch (edge)
        {
            case WmszTop:
            case WmszTopLeft:
            case WmszTopRight:
                rc.Top = rc.Bottom - targetH;
                break;
            default:
                rc.Bottom = rc.Top + targetH;
                break;
        }

        Marshal.StructureToPtr(rc, m.LParam, false);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_locked)
            SyncScaleFromClientSize();
        Invalidate();
    }

    // ----- Actions (reused by toolbar + context menu) ------------------------

    private async Task CopyAsync()
    {
        try
        {
            await CaptureService.CopyToClipboardAsync(_image);
        }
        catch (Exception ex)
        {
            Log.Error("Pin copy failed", ex);
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            string folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinShot");
            System.IO.Directory.CreateDirectory(folder);
            using var dialog = new WF.SaveFileDialog
            {
                FileName = _settings is null
                    ? CaptureService.DefaultFileName("png")
                    : FileNamer.Next(_settings, "png"),
                InitialDirectory = folder,
                Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            var copy = CaptureService.CloneBitmap(_image);
            await Task.Run(() =>
            {
                using (copy)
                    ImageSaver.Save(copy, dialog.FileName);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Pin save failed", ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static void FillRoundedRect(SD.Graphics g, SD.Brush brush, SD.Rectangle bounds, int radius)
    {
        using var path = RoundedPath(bounds, radius);
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRect(SD.Graphics g, SD.Pen pen, SD.Rectangle bounds, int radius)
    {
        using var path = RoundedPath(new SD.Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), radius);
        g.DrawPath(pen, path);
    }

    private static SD.Drawing2D.GraphicsPath RoundedPath(SD.Rectangle bounds, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new SD.Drawing2D.GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return path;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private sealed class ToolbarButton(string glyph, string tip, Action action)
    {
        public string Glyph { get; } = glyph;
        public string Tip { get; } = tip;
        public Action Action { get; } = action;
    }
}
