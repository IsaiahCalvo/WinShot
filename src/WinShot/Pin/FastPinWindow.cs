using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinShot.Core;
using WinShot.Overlay;
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
    private const int CsDropShadow = 0x00020000;

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

    private const int ResizeBorderLogical = 6;
    private const int ToolbarButtonSizeLogical = 28;
    private const int ToolbarButtonGapLogical = 4;
    private const int ToolbarPadLogical = 6;
    private const int ToolbarTopLogical = 4;
    private const int CascadeOffsetLogical = 24;
    private const int ReadoutDurationMs = 800;

    private const int ToolbarIconSizeLogical = 16;
    private static readonly SD.Color PressedFill = SD.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

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
    private readonly bool _roundedCorners;
    private readonly bool _shadow;
    private readonly bool _border;
    private readonly QuickAccessOverlayVisuals _visuals =
        QuickAccessOverlayThemePalette.For(QuickAccessOverlayThemePalette.Current);
    private readonly WF.Timer _tooltipTimer = new() { Interval = 300 };
    private QuickAccessTooltipWindow? _tooltipWindow;
    private double _scale;
    private bool _locked;
    private double _opacityBeforeLock = 1.0;
    private bool _mouseInside;
    private int _hoverButton = -1;
    private int _pressedButton = -1;
    private int _focusButton = -1;
    private string? _readoutText;
    private SD.Point _readoutPoint;

    public FastPinWindow(SD.Bitmap image, SettingsService? settings = null)
    {
        _image = image;
        _settings = settings;
        _roundedCorners = settings?.Current.PinnedRoundedCorners ?? false;
        _shadow = settings?.Current.PinnedShadow ?? false;
        _border = settings?.Current.PinnedBorder ?? false;
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
        AccessibleName = "Pinned screenshot";
        AccessibleDescription = "Pinned screenshot. Drag anywhere to move it. Press Tab to reach Copy, Save, Lock, and Close. Use the arrow keys to move the pin, or middle-click to close it.";

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        _menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkDropDown.DarkMenuRenderer(),
            ShowImageMargin = false,
        };
        var copyItem = new WF.ToolStripMenuItem("Copy", null, async (_, _) => await CopyAsync())
        {
            ToolTipText = "Copy the pinned screenshot to the clipboard",
        };
        _menu.Items.Add(copyItem);
        var saveItem = new WF.ToolStripMenuItem("Save...", null, async (_, _) => await SaveAsync())
        {
            ToolTipText = "Save the pinned screenshot to a local file",
        };
        _menu.Items.Add(saveItem);
        _lockItem = new WF.ToolStripMenuItem("Lock (Ctrl+L)", null, (_, _) => SetLocked(!_locked));
        _lockItem.ToolTipText = "Toggle click-through mode for this pinned screenshot";
        _menu.Items.Add(_lockItem);
        _menu.Items.Add(new WF.ToolStripSeparator());
        var closeItem = new WF.ToolStripMenuItem("Close", null, (_, _) => Close())
        {
            ToolTipText = "Close this pinned screenshot",
        };
        _menu.Items.Add(closeItem);
        ContextMenuStrip = _menu;

        // The hover toolbar reuses the exact actions the context menu already exposes.
        _toolbarButtons.Add(new ToolbarButton("quick-access-copy.svg", "Copy", () => _ = CopyAsync()));
        _toolbarButtons.Add(new ToolbarButton("quick-access-save.svg", "Save", () => _ = SaveAsync()));
        _toolbarButtons.Add(new ToolbarButton("quick-access-unlock.svg", "Lock", () => SetLocked(!_locked)));
        _toolbarButtons.Add(new ToolbarButton("quick-access-close.svg", "Close", Close));

        var area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        _scale = Math.Min(1.0, Math.Min(area.Width * 0.6 / _naturalWidth, area.Height * 0.6 / _naturalHeight));
        ApplyScale();

        int cascadeIndex = _openCount++ % 8;
        Location = PinInteraction.CascadeLocation(
            area,
            Size,
            cascadeIndex,
            CascadeOffsetLogical,
            DeviceDpi);

        MouseEnter += (_, _) => { _mouseInside = true; Invalidate(); };
        MouseLeave += (_, _) => { _mouseInside = false; SetHoverButton(-1); SetPressed(-1); Invalidate(); };
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
        GotFocus += (_, _) => Invalidate(ToolbarBounds());
        LostFocus += (_, _) => Invalidate(ToolbarBounds());
        _readoutTimer.Tick += (_, _) =>
        {
            _readoutTimer.Stop();
            _readoutText = null;
            Invalidate();
        };
        _tooltipTimer.Tick += (_, _) => ShowActionTooltip();
        Closed += (_, _) =>
        {
            _readoutTimer.Dispose();
            HideActionTooltip();
            _tooltipTimer.Dispose();
            _menu.Dispose(); // not parented into Controls, so Form disposal never reaches it
            OpenPins.Remove(this);
            DisposeImageWhenCopyDone();
            MemoryCleanup.Request();
        };
        OpenPins.Add(this);
    }

    protected override WF.CreateParams CreateParams
    {
        get
        {
            WF.CreateParams parameters = base.CreateParams;
            if (_shadow)
                parameters.ClassStyle |= CsDropShadow;
            return parameters;
        }
    }

    protected override WF.AccessibleObject CreateAccessibilityInstance() =>
        new FastPinAccessibleObject(this);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedRegion();
    }

    public static void UnlockAllPins()
    {
        foreach (var pin in OpenPins.ToList())
            pin.SetLocked(false);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(_image, new SD.Rectangle(1, 1, Math.Max(1, ClientSize.Width - 2), Math.Max(1, ClientSize.Height - 2)));

        if (_border)
        {
            using var border = new SD.Pen(ThemePalette.BorderStrong, Ui(1));
            DrawWindowBorder(e.Graphics, border);
        }

        // No accent outline on hover: the appearing toolbar itself is the hover signal,
        // matching the Quick Access card's restrained hover language.
        if (ToolbarVisible)
            DrawToolbar(e.Graphics);

        if (_locked)
            DrawLockBadge(e.Graphics);

        if (_readoutText is not null)
            DrawReadout(e.Graphics, _readoutText, _readoutPoint);

        base.OnPaint(e);
    }

    // ----- Hover toolbar -----------------------------------------------------

    private bool ToolbarVisible => !_locked && (_mouseInside || (_focusButton >= 0 && ContainsFocus));

    private int Ui(int logicalPixels) => PinInteraction.ScaleLogical(logicalPixels, DeviceDpi);

    private SD.Rectangle ToolbarBounds()
    {
        int count = _toolbarButtons.Count;
        int buttonSize = Ui(ToolbarButtonSizeLogical);
        int gap = Ui(ToolbarButtonGapLogical);
        int pad = Ui(ToolbarPadLogical);
        int rowWidth = count * buttonSize + (count - 1) * gap;
        int width = rowWidth + pad * 2;
        int height = buttonSize + pad * 2;
        int x = Math.Max(1, (ClientSize.Width - width) / 2);
        int y = Ui(ToolbarTopLogical);
        return new SD.Rectangle(x, y, width, height);
    }

    private SD.Rectangle ButtonBounds(int index)
    {
        SD.Rectangle bar = ToolbarBounds();
        int buttonSize = Ui(ToolbarButtonSizeLogical);
        int gap = Ui(ToolbarButtonGapLogical);
        int pad = Ui(ToolbarPadLogical);
        int x = bar.Left + pad + index * (buttonSize + gap);
        int y = bar.Top + pad;
        return new SD.Rectangle(x, y, buttonSize, buttonSize);
    }

    private void DrawToolbar(SD.Graphics g)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        SD.Rectangle bar = ToolbarBounds();

        using (var bg = new SD.SolidBrush(SD.Color.FromArgb(235, ThemePalette.ToolbarBg)))
        using (var border = new SD.Pen(ThemePalette.Border))
        {
            FillRoundedRect(g, bg, bar, Ui(8));
            DrawRoundedRect(g, border, bar, Ui(8));
        }

        for (int i = 0; i < _toolbarButtons.Count; i++)
            DrawButton(
                g,
                ButtonBounds(i),
                IconAssetFor(i),
                i == _hoverButton,
                i == _pressedButton,
                i == _focusButton && ContainsFocus);
    }

    private string IconAssetFor(int index)
    {
        // The lock toggle reflects current state: closed padlock when locked, open when unlocked.
        if (string.Equals(_toolbarButtons[index].Tip, "Lock", StringComparison.Ordinal))
            return _locked ? "quick-access-lock.svg" : "quick-access-unlock.svg";
        return _toolbarButtons[index].IconAsset;
    }

    private void DrawButton(SD.Graphics g, SD.Rectangle bounds, string iconAsset, bool hot, bool pressed, bool focused)
    {
        // Mirrors FastQuickActionsWindow.DrawButton: rest = dim glyph, hover = HoverFill circle,
        // pressed = one step brighter than hover.
        if (hot || pressed)
        {
            using var fill = new SD.SolidBrush(pressed ? PressedFill : ThemePalette.HoverFill);
            g.FillEllipse(fill, bounds);
        }

        SD.Color glyphColor = hot || pressed ? ThemePalette.TextPrimary : ThemePalette.TextSecondary;
        SD.Bitmap? icon = SvgIcons.Get(iconAsset, Ui(ToolbarIconSizeLogical), glyphColor);
        if (icon is not null)
        {
            g.DrawImageUnscaled(
                icon,
                bounds.Left + (bounds.Width - icon.Width) / 2,
                bounds.Top + (bounds.Height - icon.Height) / 2);
        }

        if (focused)
        {
            // Dotted ring following the circular button, like the card's DrawFocus.
            using var pen = new SD.Pen(ThemePalette.TextPrimary, 1)
            {
                DashStyle = SD.Drawing2D.DashStyle.Dot,
            };
            g.DrawEllipse(pen, SD.Rectangle.Inflate(bounds, -2, -2));
        }
    }

    private void DrawLockBadge(SD.Graphics g)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        var badge = new SD.Rectangle(Ui(6), Ui(6), Ui(22), Ui(22));
        using var bg = new SD.SolidBrush(SD.Color.FromArgb(217, ThemePalette.ToolbarBg));
        using var border = new SD.Pen(ThemePalette.Accent);
        g.FillEllipse(bg, badge);
        g.DrawEllipse(border, badge);

        SD.Bitmap? icon = SvgIcons.Get("quick-access-lock.svg", Ui(12), ThemePalette.TextPrimary);
        if (icon is not null)
        {
            g.DrawImageUnscaled(
                icon,
                badge.Left + (badge.Width - icon.Width) / 2,
                badge.Top + (badge.Height - icon.Height) / 2);
        }
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
        _tooltipTimer.Stop();
        HideActionTooltip();
        if (index >= 0)
            _tooltipTimer.Start();
        Invalidate(ToolbarBounds());
    }

    private void SetPressed(int index)
    {
        if (_pressedButton == index)
            return;
        _pressedButton = index;
        Invalidate(ToolbarBounds());
    }

    private string TooltipTextFor(int index) => _toolbarButtons[index].Tip switch
    {
        "Copy" => "Copy (Ctrl+C)",
        "Save" => "Save (Ctrl+S)",
        "Lock" => _locked ? "Unlock — Ctrl+L" : "Lock (click-through) — Ctrl+L",
        _ => _toolbarButtons[index].Tip,
    };

    private void ShowActionTooltip()
    {
        // Same 300ms labeling pattern as FastQuickActionsWindow.ShowActionTooltip.
        _tooltipTimer.Stop();
        if (IsDisposed || !Visible || !ToolbarVisible || _hoverButton < 0 || _hoverButton >= _toolbarButtons.Count)
            return;

        var tooltip = new QuickAccessTooltipWindow(TooltipTextFor(_hoverButton), _visuals, DeviceDpi);
        _tooltipWindow = tooltip;
        tooltip.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_tooltipWindow, tooltip))
                _tooltipWindow = null;
        };
        tooltip.ShowBelow(this, RectangleToScreen(ButtonBounds(_hoverButton)));
    }

    private void HideActionTooltip()
    {
        QuickAccessTooltipWindow? tooltip = _tooltipWindow;
        _tooltipWindow = null;
        if (tooltip is null || tooltip.IsDisposed)
            return;

        tooltip.Close();
        tooltip.Dispose();
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
        if (e.Button == WF.MouseButtons.Middle)
        {
            Close();
            return;
        }

        if (e.Button != WF.MouseButtons.Left)
            return;

        // A click on a toolbar button must not start a window drag; it fires on MouseUp.
        int button = HitTestButton(e.Location);
        if (button >= 0)
        {
            FocusToolbarButton(button);
            SetPressed(button);
            return;
        }

        _focusButton = -1;

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

        SetPressed(-1);
        int index = HitTestButton(e.Location);
        if (index >= 0)
            _toolbarButtons[index].Action();
    }

    private void OnPinMouseWheel(object? sender, WF.MouseEventArgs e)
    {
        if ((ModifierKeys & WF.Keys.Control) == WF.Keys.Control)
        {
            Opacity = PinInteraction.AdjustOpacity(Opacity, e.Delta);
            ShowReadout($"Opacity {(int)Math.Round(Opacity * 100)}%", PointToClient(WF.Cursor.Position));
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

        if (e.KeyCode == WF.Keys.C && e.Control)
        {
            _ = CopyAsync();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == WF.Keys.S && e.Control)
        {
            _ = SaveAsync();
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

    protected override bool ProcessCmdKey(ref WF.Message msg, WF.Keys keyData)
    {
        WF.Keys keyCode = keyData & WF.Keys.KeyCode;
        WF.Keys modifiers = keyData & WF.Keys.Modifiers;

        if (keyCode == WF.Keys.Tab && modifiers is WF.Keys.None or WF.Keys.Shift)
        {
            int direction = modifiers == WF.Keys.Shift ? -1 : 1;
            int next = _focusButton < 0
                ? (direction > 0 ? 0 : _toolbarButtons.Count - 1)
                : (_focusButton + direction + _toolbarButtons.Count) % _toolbarButtons.Count;
            FocusToolbarButton(next);
            return true;
        }

        if (_focusButton >= 0 && modifiers == WF.Keys.None &&
            keyCode is WF.Keys.Enter or WF.Keys.Space)
        {
            _toolbarButtons[_focusButton].Action();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void FocusToolbarButton(int index)
    {
        if (_locked || index < 0 || index >= _toolbarButtons.Count)
            return;

        _focusButton = index;
        Focus();
        AccessibilityNotifyClients(WF.AccessibleEvents.Focus, index + 1);
        Invalidate(ToolbarBounds());
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
            _focusButton = -1;
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
        int resizeBorder = Ui(ResizeBorderLogical);
        bool left = p.X <= resizeBorder;
        bool right = p.X >= w - resizeBorder;
        bool top = p.Y <= resizeBorder;
        bool bottom = p.Y >= h - resizeBorder;

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
        ApplyRoundedRegion();
        Invalidate();
    }

    protected override void OnDpiChanged(WF.DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyRoundedRegion();
        Invalidate();
    }

    private void ApplyRoundedRegion()
    {
        SD.Region? next = null;
        if (_roundedCorners && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            using var path = PinWindowGeometry.RoundedClipPath(ClientSize, DeviceDpi);
            next = new SD.Region(path);
        }

        SD.Region? previous = Region;
        Region = next;
        previous?.Dispose();
    }

    private void DrawWindowBorder(SD.Graphics graphics, SD.Pen pen) =>
        PinWindowGeometry.DrawBorder(
            graphics,
            pen,
            ClientSize,
            DeviceDpi,
            _roundedCorners);

    // ----- Actions (reused by toolbar + context menu) ------------------------

    private Task? _copyTask;

    private async Task CopyAsync()
    {
        try
        {
            // Tracked so Close never disposes _image while the copy's own STA thread
            // is still cloning it (which silently dropped the copy).
            _copyTask = CaptureService.CopyToClipboardAsync(_image);
            await _copyTask;
        }
        catch (Exception ex)
        {
            Log.Error("Pin copy failed", ex);
        }
    }

    private void DisposeImageWhenCopyDone()
    {
        Task? pending = _copyTask;
        if (pending is null || pending.IsCompleted)
        {
            _image.Dispose();
            return;
        }

        _ = pending.ContinueWith(
            _ => _image.Dispose(),
            TaskContinuationOptions.ExecuteSynchronously);
    }

    private async Task SaveAsync()
    {
        try
        {
            string fallbackFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinShot");
            string folder = PinInteraction.ResolveSaveFolder(_settings?.Current.SaveFolder, fallbackFolder);
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
        using var path = GdiPaths.RoundedRect(bounds, radius);
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRect(SD.Graphics g, SD.Pen pen, SD.Rectangle bounds, int radius)
    {
        using var path = GdiPaths.RoundedRect(new SD.Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), radius);
        g.DrawPath(pen, path);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private sealed class FastPinAccessibleObject(FastPinWindow owner)
        : WF.Control.ControlAccessibleObject(owner)
    {
        public override string? Name
        {
            get => owner.AccessibleName;
            set => owner.AccessibleName = value;
        }

        public override string? Description => owner.AccessibleDescription;
        public override WF.AccessibleRole Role => WF.AccessibleRole.Window;

        public override int GetChildCount() => owner._toolbarButtons.Count;

        public override WF.AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner._toolbarButtons.Count
                ? new ToolbarButtonAccessibleObject(owner, index, this)
                : null;

        public override WF.AccessibleObject? GetFocused() =>
            owner._focusButton >= 0 ? GetChild(owner._focusButton) : base.GetFocused();

        public override WF.AccessibleObject? HitTest(int x, int y)
        {
            SD.Point client = owner.PointToClient(new SD.Point(x, y));
            int button = owner.HitTestButton(client);
            return button >= 0 ? GetChild(button) : base.HitTest(x, y);
        }
    }

    private sealed class ToolbarButtonAccessibleObject(
        FastPinWindow owner,
        int index,
        WF.AccessibleObject parent) : WF.AccessibleObject
    {
        public override string? Name
        {
            get => owner._toolbarButtons[index].Tip;
            set { }
        }

        public override string? Description => owner._toolbarButtons[index].Help;
        public override string? Help => owner._toolbarButtons[index].Help;
        public override string? DefaultAction => "Press";
        public override WF.AccessibleRole Role => WF.AccessibleRole.PushButton;
        public override WF.AccessibleObject? Parent => parent;
        public override SD.Rectangle Bounds => owner.RectangleToScreen(owner.ButtonBounds(index));

        public override WF.AccessibleStates State
        {
            get
            {
                WF.AccessibleStates state = WF.AccessibleStates.Focusable |
                                            WF.AccessibleStates.Selectable;
                if (owner._focusButton == index && owner.ContainsFocus)
                    state |= WF.AccessibleStates.Focused | WF.AccessibleStates.Selected;
                if (owner._locked)
                    state |= WF.AccessibleStates.Unavailable;
                return state;
            }
        }

        public override void DoDefaultAction()
        {
            if (!owner._locked)
                owner._toolbarButtons[index].Action();
        }

        public override void Select(WF.AccessibleSelection flags)
        {
            if ((flags & (WF.AccessibleSelection.TakeFocus | WF.AccessibleSelection.TakeSelection)) != 0)
                owner.FocusToolbarButton(index);
        }

        public override WF.AccessibleObject? Navigate(WF.AccessibleNavigation navdir) => navdir switch
        {
            WF.AccessibleNavigation.Next or WF.AccessibleNavigation.Right =>
                parent.GetChild((index + 1) % owner._toolbarButtons.Count),
            WF.AccessibleNavigation.Previous or WF.AccessibleNavigation.Left =>
                parent.GetChild((index - 1 + owner._toolbarButtons.Count) % owner._toolbarButtons.Count),
            WF.AccessibleNavigation.Up => parent,
            _ => base.Navigate(navdir),
        };
    }

    private sealed class ToolbarButton(string iconAsset, string tip, Action action)
    {
        public string IconAsset { get; } = iconAsset;
        public string Tip { get; } = tip;
        public string Help { get; } = tip switch
        {
            "Copy" => "Copy the pinned screenshot to the clipboard",
            "Save" => "Save the pinned screenshot to a local file",
            "Lock" => "Toggle click-through mode for this pinned screenshot",
            "Close" => "Close this pinned screenshot",
            _ => tip,
        };
        public Action Action { get; } = action;
    }
}
