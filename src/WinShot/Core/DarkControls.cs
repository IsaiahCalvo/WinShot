using System.Drawing.Drawing2D;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>Shared palette for the owner-drawn dark controls below.</summary>
internal static class DarkControlPalette
{
    public static readonly SD.Color Card = ThemePalette.ToolbarBg;
    public static readonly SD.Color Field = ThemePalette.SurfaceAlt;
    public static readonly SD.Color FieldHot = ThemePalette.SurfaceHover;
    public static readonly SD.Color FieldPressed = SD.Color.FromArgb(46, 46, 49);
    public static readonly SD.Color Text = ThemePalette.TextPrimary;
    public static readonly SD.Color TextMuted = ThemePalette.TextSecondary;
    public static readonly SD.Color Hairline = ThemePalette.Border;

    // Clamp BOTH ends: a negative amount on a low channel (Accent's red is 0x0A)
    // otherwise throws inside OnPaint, and WinForms brands the control with a
    // permanent red-X placeholder after a single paint exception.
    public static SD.Color Lighten(SD.Color c, int amount) => SD.Color.FromArgb(
        c.A,
        Math.Clamp(c.R + amount, 0, 255),
        Math.Clamp(c.G + amount, 0, 255),
        Math.Clamp(c.B + amount, 0, 255));

    /// <summary>Fractional <see cref="Lighten"/>, so a hover can ease in instead of stepping.</summary>
    public static SD.Color Lighten(SD.Color c, double amount) => Lighten(c, (int)Math.Round(amount));

    /// <summary>Straight channel lerp; <paramref name="amount"/> 0 gives <paramref name="from"/>.</summary>
    public static SD.Color Mix(SD.Color from, SD.Color to, double amount)
    {
        double t = Math.Clamp(amount, 0d, 1d);
        return SD.Color.FromArgb(
            (int)Math.Round(from.A + ((to.A - from.A) * t)),
            (int)Math.Round(from.R + ((to.R - from.R) * t)),
            (int)Math.Round(from.G + ((to.G - from.G) * t)),
            (int)Math.Round(from.B + ((to.B - from.B) * t)));
    }

    /// <summary>
    /// How far to lighten a button fill given how far into hover and press it is.
    /// Fully pressed reads the same as the old discrete -12 even though the pointer
    /// is necessarily also hovering: press does not stack on hover, it overrides it.
    /// </summary>
    public static double HoverPressLighten(double hover, double press)
        => (18d * Math.Clamp(hover, 0d, 1d)) - (30d * Math.Clamp(press, 0d, 1d));
}

/// <summary>
/// A 0..1 amount that eases toward its target over <see cref="Motion.QuickMs"/> and
/// repaints its owner each frame, so an owner-drawn WinForms control cross-fades
/// between states the same way the WPF templates do instead of snapping.
///
/// Every owner must dispose it: an in-flight tween holds the process-wide
/// high-resolution clock, and a control that goes away mid-fade would pin it.
/// </summary>
internal sealed class StateAmount : IDisposable
{
    private readonly WF.Control _owner;
    private IDisposable? _run;
    private double _from;
    private double _target;
    private bool _disposed;

    public StateAmount(WF.Control owner) => _owner = owner;

    public double Value { get; private set; }

    public void To(double target)
    {
        // A control disposed while the pointer is still over it gets one last OnMouseLeave.
        // Starting a tween from there raised the process-wide timer resolution for 140 ms on
        // behalf of a control nobody can see, and if the pump had already stopped the
        // Motion holder would never have been released at all.
        if (_disposed || Math.Abs(target - _target) < 0.0001d)
            return;

        _run?.Dispose();
        _from = Value;
        _target = target;
        _run = Motion.Run(Motion.QuickMs, Motion.EaseOutCubic, eased =>
        {
            Value = _from + ((_target - _from) * eased);
            if (!_owner.IsDisposed)
                _owner.Invalidate();
        });
    }

    /// <summary>
    /// Cancels any tween and puts the amount at <paramref name="value"/> without animating.
    /// Used when a wash is handed from one cell to another: the outgoing amount picks up
    /// exactly where the incoming one had reached, so the cross-fade has no seam.
    /// </summary>
    public void Reset(double value)
    {
        _run?.Dispose();
        _run = null;
        Value = _from = _target = Math.Clamp(value, 0d, 1d);
    }

    public void Dispose()
    {
        _disposed = true;
        _run?.Dispose();
        _run = null;
    }
}

/// <summary>
/// Owner-drawn rounded button replacing the stock flat-rectangle WinForms button on
/// the app's dark popup cards. Implements IButtonControl so it still works as a
/// form's Accept/Cancel button. Layout units are logical 96-DPI pixels multiplied
/// by <see cref="Scale"/>.
/// </summary>
public sealed class DarkButton : WF.Control, WF.IButtonControl
{
    private readonly StateAmount _hover;
    private readonly StateAmount _press;
    private bool _isDefault;

    public DarkButton()
    {
        _hover = new StateAmount(this);
        _press = new StateAmount(this);
        SetStyle(
            WF.ControlStyles.UserPaint |
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.StandardClick |
            WF.ControlStyles.Selectable,
            true);
        BackColor = DarkControlPalette.Card;
        Cursor = WF.Cursors.Hand;
        ForeColor = DarkControlPalette.Text;
        // A custom-painted WF.Control announces itself as a generic client area, so the
        // toast's "Open link" and every button in the WinForms dialogs were unnamed shapes
        // to a screen reader. AccessibleName follows Text on its own.
        AccessibleRole = WF.AccessibleRole.PushButton;
    }

    public double Scale { get; init; } = 1.0;

    /// <summary>Resting fill. Hover/press shades derive from it.</summary>
    public SD.Color FillColor { get; set; } = DarkControlPalette.Field;

    public int CornerRadius { get; set; } = 6;

    public SD.ContentAlignment TextAlign { get; set; } = SD.ContentAlignment.MiddleCenter;

    public WF.DialogResult DialogResult { get; set; }

    private int S(int logical) => (int)Math.Round(logical * Scale);

    public void NotifyDefault(bool value)
    {
        if (_isDefault == value)
            return;
        _isDefault = value;
        Invalidate();
    }

    public void PerformClick() => OnClick(EventArgs.Empty);

    protected override void OnClick(EventArgs e)
    {
        if (DialogResult != WF.DialogResult.None && FindForm() is WF.Form form)
            form.DialogResult = DialogResult;
        base.OnClick(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover.To(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover.To(0); _press.To(0); base.OnMouseLeave(e); }
    protected override void OnMouseDown(WF.MouseEventArgs e) { _press.To(1); base.OnMouseDown(e); }
    protected override void OnMouseUp(WF.MouseEventArgs e)
    {
        // A 20-30 ms click is the normal case, and releasing from wherever the 140 ms press
        // ease had reached peaked the wash at about 0.15 — four of the thirty shades it can
        // subtract, so the commit read as nothing happening. Land the press first, the same
        // way the keyboard branch does, then ease it off. A press held long enough to finish
        // is already at 1, so this changes nothing for a deliberate press.
        _press.Reset(1);
        _press.To(0);
        base.OnMouseUp(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hover.Dispose();
            _press.Dispose();
        }
        base.Dispose(disposing);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override bool ProcessDialogKey(WF.Keys keyData)
    {
        if (keyData is WF.Keys.Enter or WF.Keys.Space && Focused)
        {
            // The mouse gets a press wash on the way in and out; the keyboard fired the
            // action with the button looking untouched, so nothing acknowledged the commit.
            // A key has no down/up to straddle, so the tap is full press easing straight off.
            _press.Reset(1);
            _press.To(0);
            PerformClick();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        SD.Color fill = !Enabled
            ? DarkControlPalette.Lighten(DarkControlPalette.Card, 8)
            : DarkControlPalette.Lighten(
                FillColor,
                DarkControlPalette.HoverPressLighten(_hover.Value, _press.Value));

        var rect = new SD.Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = GdiPaths.RoundedRect(rect, S(CornerRadius)))
        {
            using var brush = new SD.SolidBrush(fill);
            g.FillPath(brush, path);
            // The default button gets a soft accent outline instead of a fill change,
            // so Enter's target reads without shouting. Plain focus only rings after
            // actual keyboard navigation (ShowFocusCues) — otherwise the first button
            // on a HUD silently holds focus and looks different from its siblings.
            if (_isDefault || (Focused && ShowFocusCues))
            {
                using var pen = new SD.Pen(SD.Color.FromArgb(150, ThemePalette.AccentHover), 1f);
                g.DrawPath(pen, path);
            }
        }

        SD.Color textColor = Enabled ? ForeColor : SD.Color.FromArgb(120, 200, 200, 200);
        var flags = WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.EndEllipsis;
        var textRect = ClientRectangle;
        if (TextAlign == SD.ContentAlignment.MiddleLeft)
        {
            flags |= WF.TextFormatFlags.Left;
            textRect = new SD.Rectangle(Padding.Left, 0, Math.Max(1, Width - Padding.Left), Height);
        }
        else
        {
            flags |= WF.TextFormatFlags.HorizontalCenter;
        }
        WF.TextRenderer.DrawText(g, Text, Font, textRect, textColor, flags);
    }
}

/// <summary>
/// Owner-drawn dropdown: a rounded dark field with the value and a chevron, opening a
/// dark-styled menu. Replaces WinForms ComboBox, whose drop button ignores the theme.
/// </summary>
public sealed class DarkDropDown : WF.Control
{
    private readonly WF.ContextMenuStrip _menu = new();
    private readonly StateAmount _hover;
    private int _selectedIndex = -1;

    public DarkDropDown()
    {
        _hover = new StateAmount(this);
        SetStyle(
            WF.ControlStyles.UserPaint |
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.Selectable,
            true);
        BackColor = DarkControlPalette.Card;
        Cursor = WF.Cursors.Hand;
        ForeColor = DarkControlPalette.Text;
        _menu.Renderer = new DarkMenuRenderer();
        _menu.ShowImageMargin = false;
        Disposed += (_, _) => { _menu.Dispose(); _hover.Dispose(); };
        MouseEnter += (_, _) => _hover.To(1);
        MouseLeave += (_, _) => _hover.To(0);
    }

    public double Scale { get; init; } = 1.0;

    public List<string> Items { get; } = new();

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int clamped = Math.Min(value, Items.Count - 1);
            if (_selectedIndex == clamped)
                return;
            _selectedIndex = clamped;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedItem =>
        _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;

    private int S(int logical) => (int)Math.Round(logical * Scale);

    protected override void OnMouseDown(WF.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != WF.MouseButtons.Left)
            return;
        Focus();
        OpenMenu();
    }

    protected override bool ProcessDialogKey(WF.Keys keyData)
    {
        if (Focused)
        {
            switch (keyData)
            {
                case WF.Keys.Space:
                case WF.Keys.Enter:
                case WF.Keys.Down | WF.Keys.Alt:
                    OpenMenu();
                    return true;
                case WF.Keys.Up:
                    if (_selectedIndex > 0) SelectedIndex = _selectedIndex - 1;
                    return true;
                case WF.Keys.Down:
                    if (_selectedIndex < Items.Count - 1) SelectedIndex = _selectedIndex + 1;
                    return true;
            }
        }
        return base.ProcessDialogKey(keyData);
    }

    private void OpenMenu()
    {
        _menu.Items.Clear();
        for (int i = 0; i < Items.Count; i++)
        {
            int index = i;
            var item = new WF.ToolStripMenuItem(Items[i])
            {
                Checked = index == _selectedIndex,
            };
            item.Click += (_, _) => SelectedIndex = index;
            _menu.Items.Add(item);
        }
        _menu.MinimumSize = new SD.Size(Width, 0);
        _menu.Show(this, new SD.Point(0, Height + 2));
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        SD.Color fill = DarkControlPalette.Mix(
            DarkControlPalette.Field, DarkControlPalette.FieldHot, _hover.Value);
        var rect = new SD.Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = GdiPaths.RoundedRect(rect, S(5)))
        {
            using var brush = new SD.SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new SD.Pen(
                Focused ? SD.Color.FromArgb(150, ThemePalette.AccentHover) : DarkControlPalette.Hairline,
                1f);
            g.DrawPath(pen, path);
        }

        int pad = S(10);
        int chevronBox = S(16);
        var textRect = new SD.Rectangle(pad, 0, Math.Max(1, Width - pad * 2 - chevronBox), Height);
        WF.TextRenderer.DrawText(
            g,
            SelectedItem ?? string.Empty,
            Font,
            textRect,
            Enabled ? ForeColor : SD.Color.FromArgb(120, 200, 200, 200),
            WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.Left | WF.TextFormatFlags.EndEllipsis);

        // Chevron.
        using var chevron = new SD.Pen(DarkControlPalette.TextMuted, Math.Max(1.4f, (float)(1.4 * Scale)))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        float cx = Width - pad - chevronBox / 2f;
        float cy = Height / 2f;
        float half = S(4);
        g.DrawLines(chevron, new[]
        {
            new SD.PointF(cx - half, cy - half / 2f),
            new SD.PointF(cx, cy + half / 2f),
            new SD.PointF(cx + half, cy - half / 2f),
        });
    }

    /// <summary>Dark menu styling for the dropdown popup (and reusable elsewhere).</summary>
    public class DarkMenuRenderer : WF.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors())
        {
        }

        protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected || e.Item.Pressed)
            {
                using var brush = new SD.SolidBrush(SD.Color.FromArgb(66, 66, 70));
                e.Graphics.FillRectangle(brush, new SD.Rectangle(SD.Point.Empty, e.Item.Size));
            }
        }

        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? DarkControlPalette.Text : DarkControlPalette.TextMuted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
        {
            // Accent dot instead of the stock checkmark bitmap.
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SD.SolidBrush(ThemePalette.Accent);
            var r = e.ImageRectangle;
            int d = Math.Min(8, Math.Min(r.Width, r.Height));
            g.FillEllipse(brush, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        }

        private sealed class DarkMenuColors : WF.ProfessionalColorTable
        {
            private static readonly SD.Color Back = SD.Color.FromArgb(35, 35, 38);
            public override SD.Color ToolStripDropDownBackground => Back;
            public override SD.Color ImageMarginGradientBegin => Back;
            public override SD.Color ImageMarginGradientMiddle => Back;
            public override SD.Color ImageMarginGradientEnd => Back;
            public override SD.Color MenuBorder => SD.Color.FromArgb(70, 70, 74);
            public override SD.Color MenuItemBorder => SD.Color.Transparent;
            public override SD.Color SeparatorDark => SD.Color.FromArgb(70, 70, 74);
            public override SD.Color SeparatorLight => Back;
        }
    }
}

/// <summary>
/// A rounded dark numeric field: draws its own chrome and hosts a borderless
/// TextBox vertically centered inside (a bare WinForms TextBox pins its text to
/// the top of any taller box).
/// </summary>
public sealed class DarkNumberBox : WF.Control
{
    private readonly WF.TextBox _inner = new()
    {
        BackColor = DarkControlPalette.Field,
        BorderStyle = WF.BorderStyle.None,
        ForeColor = DarkControlPalette.Text,
        TextAlign = WF.HorizontalAlignment.Center,
    };

    private readonly StateAmount _focus;

    public DarkNumberBox()
    {
        _focus = new StateAmount(this);
        SetStyle(
            WF.ControlStyles.UserPaint |
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw,
            true);
        BackColor = DarkControlPalette.Card;
        Controls.Add(_inner);
        _inner.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _inner.GotFocus += (_, _) => _focus.To(1);
        _inner.LostFocus += (_, _) => _focus.To(0);
        Resize += (_, _) => LayoutInner();
    }

    public double Scale { get; init; } = 1.0;

    private int S(int logical) => (int)Math.Round(logical * Scale);

    public override string Text
    {
        get => _inner.Text;
        set => _inner.Text = value;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _inner.Font = Font;
        LayoutInner();
    }

    private void LayoutInner()
    {
        int margin = S(6);
        _inner.Width = Math.Max(1, Width - margin * 2);
        _inner.Left = margin;
        _inner.Top = Math.Max(0, (Height - _inner.Height) / 2) + 1;
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = GdiPaths.RoundedRect(new SD.Rectangle(0, 0, Width - 1, Height - 1), S(5));
        using var brush = new SD.SolidBrush(DarkControlPalette.Field);
        g.FillPath(brush, path);
        using var pen = new SD.Pen(
            DarkControlPalette.Mix(
                DarkControlPalette.Hairline,
                SD.Color.FromArgb(150, ThemePalette.AccentHover),
                _focus.Value),
            1f);
        g.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing)
    {
        // An in-flight tween holds the process-wide high-resolution clock.
        if (disposing)
            _focus.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Two-or-more option segmented switch (e.g. MP4 | GIF): one rounded track, the
/// selected cell filled with the accent. Replaces radio buttons on popup cards so
/// the only toggle glyphs left are checkboxes.
/// </summary>
public sealed class DarkSegmented : WF.Control
{
    // The selection pill slides between cells rather than reappearing in the new one,
    // which is what makes a segmented control read as one moving thing. RectangleTween
    // owns the geometry and has no clock of its own by design, so Motion.Run pumps it.
    private readonly WinShot.Capture.RectangleTween _pill = new();
    // Two amounts, not one: the cell being entered fades in while the cell being left
    // fades out. With a single amount the wash could only be painted where the pointer
    // currently is, so leaving the control ran a fade-out over nothing (hover in eased,
    // hover out snapped) and sliding between cells teleported the wash at full strength.
    private readonly StateAmount _hover;
    private readonly StateAmount _hoverOut;
    private IDisposable? _pillRun;
    private SD.Rectangle _pillRect;
    private bool _gliding;
    private int _selectedIndex;
    private int _hotIndex = -1;
    private int _coolingIndex = -1;

    public DarkSegmented()
    {
        _hover = new StateAmount(this);
        _hoverOut = new StateAmount(this);
        SetStyle(
            WF.ControlStyles.UserPaint |
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.Selectable,
            true);
        BackColor = DarkControlPalette.Card;
        Cursor = WF.Cursors.Hand;
        ForeColor = DarkControlPalette.Text;
    }

    public double Scale { get; init; } = 1.0;

    public string[] Options { get; set; } = [];

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value || value < 0 || value >= Options.Length)
                return;
            SD.Rectangle from = CurrentPill();
            _selectedIndex = value;
            GlidePillFrom(from);
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int S(int logical) => (int)Math.Round(logical * Scale);

    private int HitCell(int x) =>
        Options.Length == 0 ? -1 : Math.Clamp(x * Options.Length / Math.Max(1, Width), 0, Options.Length - 1);

    /// <summary>
    /// The pill for one cell: the cell's slice of the track, inset on every side.
    /// Pure so the geometry can be tested without a window; cells are laid out from
    /// float edges so rounding never leaves a one-pixel seam between them.
    /// </summary>
    internal static SD.Rectangle PillBounds(SD.Size size, int count, int index, int inset)
    {
        if (count <= 0 || index < 0 || index >= count)
            return SD.Rectangle.Empty;

        float cellWidth = (float)size.Width / count;
        int left = (int)(index * cellWidth);
        int right = (int)((index + 1) * cellWidth);
        return new SD.Rectangle(
            left + inset,
            inset,
            Math.Max(1, right - left - (inset * 2)),
            Math.Max(1, size.Height - (inset * 2)));
    }

    private SD.Rectangle PillFor(int index) => PillBounds(Size, Options.Length, index, S(3));

    /// <summary>Where the pill is right now — mid-glide if one is running.</summary>
    private SD.Rectangle CurrentPill() => _gliding ? _pillRect : PillFor(_selectedIndex);

    private void GlidePillFrom(SD.Rectangle from)
    {
        SD.Rectangle to = PillFor(_selectedIndex);
        _pillRun?.Dispose();

        if (from.IsEmpty || to.IsEmpty)
        {
            _gliding = false;
            Invalidate();
            return;
        }

        _pill.Retarget(from, to);
        _pillRect = from;
        _gliding = true;
        _pillRun = Motion.Run(
            Motion.GlideDurationMs,
            Motion.EaseOutCubic,
            eased =>
            {
                // The eased amount, not the tween's own clock: Motion.Run owns both here, so
                // the pill decelerates into its cell like every other state change in the app.
                _pill.Update((float)eased);
                _pillRect = _pill.Current;
                if (!IsDisposed)
                    Invalidate();
            },
            () =>
            {
                // Belt and braces: Motion.Run always delivers amount 1.0, which lands the
                // tween on its target and stops it, but a cancelled run never gets there.
                _pill.Stop();
                _gliding = false;
                Invalidate();
            });
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // A resize mid-glide would animate toward a rect that no longer exists.
        _pillRun?.Dispose();
        _pillRun = null;
        _pill.Stop();
        _gliding = false;
        // ResizeRedraw is not set on this control, so a resize that exposes no new area
        // repaints nothing and the pill stays drawn at the rect it was abandoned mid-glide.
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pillRun?.Dispose();
            _hover.Dispose();
            _hoverOut.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHotCell(HitCell(e.X));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHotCell(-1);
    }

    /// <summary>Hands the wash from whichever cell had it to <paramref name="hit"/>, both
    /// halves eased. -1 means the pointer left the control, which is just a fade to nothing.</summary>
    private void SetHotCell(int hit)
    {
        if (hit == _hotIndex)
            return;

        if (_hotIndex >= 0 && _hover.Value > 0.001d)
        {
            _coolingIndex = _hotIndex;
            _hoverOut.Reset(_hover.Value);
            _hoverOut.To(0);
        }

        _hotIndex = hit;
        _hover.Reset(0);
        if (hit >= 0)
            _hover.To(1);
        Invalidate();
    }

    protected override void OnMouseDown(WF.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == WF.MouseButtons.Left)
        {
            Focus();
            SelectedIndex = HitCell(e.X);
        }
    }

    protected override bool ProcessDialogKey(WF.Keys keyData)
    {
        if (Focused)
        {
            if (keyData == WF.Keys.Left && _selectedIndex > 0) { SelectedIndex = _selectedIndex - 1; return true; }
            if (keyData == WF.Keys.Right && _selectedIndex < Options.Length - 1) { SelectedIndex = _selectedIndex + 1; return true; }
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int radius = S(6);
        var track = new SD.Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = GdiPaths.RoundedRect(track, radius))
        {
            using var brush = new SD.SolidBrush(DarkControlPalette.Field);
            g.FillPath(brush, path);
            using var pen = new SD.Pen(DarkControlPalette.Hairline, 1f);
            g.DrawPath(pen, path);
        }

        if (Options.Length == 0)
            return;

        int pillRadius = Math.Max(2, radius - 2);

        // Hover wash first, under the pill: hovering the selected cell must not
        // repaint over the accent.
        DrawWash(g, _coolingIndex, _hoverOut.Value, pillRadius);
        DrawWash(g, _hotIndex, _hover.Value, pillRadius);

        SD.Rectangle selected = CurrentPill();
        if (!selected.IsEmpty)
        {
            using var path = GdiPaths.RoundedRect(selected, pillRadius);
            using var brush = new SD.SolidBrush(ThemePalette.Accent);
            g.FillPath(brush, path);
        }

        float cellWidth = (float)Width / Options.Length;
        for (int i = 0; i < Options.Length; i++)
        {
            var cell = new SD.Rectangle((int)(i * cellWidth), 0, (int)cellWidth, Height);
            // Colour follows the pill that is physically over the label rather than the
            // index that was already committed: mid-glide the outgoing label would
            // otherwise be muted grey on blue and the incoming one white on bare track.
            // Measured against the cell's own pill, so a resting selection is exactly 1.
            SD.Rectangle cellPill = PillFor(i);
            double covered = cellPill.Width <= 0
                ? 0d
                : Math.Clamp((double)SD.Rectangle.Intersect(selected, cellPill).Width / cellPill.Width, 0d, 1d);
            WF.TextRenderer.DrawText(
                g,
                Options[i],
                Font,
                cell,
                DarkControlPalette.Mix(DarkControlPalette.TextMuted, SD.Color.White, covered),
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.HorizontalCenter);
        }
    }

    private void DrawWash(SD.Graphics g, int index, double amount, int radius)
    {
        // The selected cell needs no wash - the pill is already sitting on it. While the pill
        // is still gliding it is NOT sitting on it yet, and dropping the wash on the frame of
        // the click left bare track under the pointer for the whole 200 ms: the press read as
        // "nothing happened" until the pill arrived.
        if (index < 0 || (index == _selectedIndex && !_gliding) || amount <= 0.001d)
            return;
        SD.Rectangle bounds = PillFor(index);
        if (bounds.IsEmpty)
            return;
        using var path = GdiPaths.RoundedRect(bounds, radius);
        using var brush = new SD.SolidBrush(DarkControlPalette.Mix(
            DarkControlPalette.Field, DarkControlPalette.FieldHot, amount));
        g.FillPath(brush, path);
    }
}
