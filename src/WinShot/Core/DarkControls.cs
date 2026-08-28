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

    public static SD.Color Lighten(SD.Color c, int amount) => SD.Color.FromArgb(
        c.A,
        Math.Min(255, c.R + amount),
        Math.Min(255, c.G + amount),
        Math.Min(255, c.B + amount));
}

/// <summary>
/// Owner-drawn rounded button replacing the stock flat-rectangle WinForms button on
/// the app's dark popup cards. Implements IButtonControl so it still works as a
/// form's Accept/Cancel button. Layout units are logical 96-DPI pixels multiplied
/// by <see cref="Scale"/>.
/// </summary>
public sealed class DarkButton : WF.Control, WF.IButtonControl
{
    private bool _hot;
    private bool _pressed;
    private bool _isDefault;

    public DarkButton()
    {
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

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(WF.MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(WF.MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override bool ProcessDialogKey(WF.Keys keyData)
    {
        if (keyData is WF.Keys.Enter or WF.Keys.Space && Focused)
        {
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
            : _pressed ? DarkControlPalette.Lighten(FillColor, -12)
            : _hot ? DarkControlPalette.Lighten(FillColor, 18)
            : FillColor;

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
    private int _selectedIndex = -1;
    private bool _hot;

    public DarkDropDown()
    {
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
        Disposed += (_, _) => _menu.Dispose();
        MouseEnter += (_, _) => { _hot = true; Invalidate(); };
        MouseLeave += (_, _) => { _hot = false; Invalidate(); };
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

        SD.Color fill = _hot ? DarkControlPalette.FieldHot : DarkControlPalette.Field;
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
    public sealed class DarkMenuRenderer : WF.ToolStripProfessionalRenderer
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

    public DarkNumberBox()
    {
        SetStyle(
            WF.ControlStyles.UserPaint |
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw,
            true);
        BackColor = DarkControlPalette.Card;
        Controls.Add(_inner);
        _inner.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _inner.GotFocus += (_, _) => Invalidate();
        _inner.LostFocus += (_, _) => Invalidate();
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
            _inner.Focused ? SD.Color.FromArgb(150, ThemePalette.AccentHover) : DarkControlPalette.Hairline,
            1f);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// Two-or-more option segmented switch (e.g. MP4 | GIF): one rounded track, the
/// selected cell filled with the accent. Replaces radio buttons on popup cards so
/// the only toggle glyphs left are checkboxes.
/// </summary>
public sealed class DarkSegmented : WF.Control
{
    private int _selectedIndex;
    private int _hotIndex = -1;

    public DarkSegmented()
    {
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
            _selectedIndex = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int S(int logical) => (int)Math.Round(logical * Scale);

    private int HitCell(int x) =>
        Options.Length == 0 ? -1 : Math.Clamp(x * Options.Length / Math.Max(1, Width), 0, Options.Length - 1);

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitCell(e.X);
        if (hit != _hotIndex)
        {
            _hotIndex = hit;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hotIndex = -1;
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

        float cellWidth = (float)Width / Options.Length;
        for (int i = 0; i < Options.Length; i++)
        {
            var cell = new SD.Rectangle((int)(i * cellWidth), 0, (int)cellWidth, Height);
            if (i == _selectedIndex)
            {
                int inset = S(3);
                var pill = new SD.Rectangle(
                    cell.X + inset, inset, cell.Width - inset * 2, Height - inset * 2);
                using var path = GdiPaths.RoundedRect(pill, Math.Max(2, radius - 2));
                using var brush = new SD.SolidBrush(ThemePalette.Accent);
                g.FillPath(brush, path);
            }
            else if (i == _hotIndex)
            {
                int inset = S(3);
                var pill = new SD.Rectangle(
                    cell.X + inset, inset, cell.Width - inset * 2, Height - inset * 2);
                using var path = GdiPaths.RoundedRect(pill, Math.Max(2, radius - 2));
                using var brush = new SD.SolidBrush(DarkControlPalette.FieldHot);
                g.FillPath(brush, path);
            }

            WF.TextRenderer.DrawText(
                g,
                Options[i],
                Font,
                cell,
                i == _selectedIndex ? SD.Color.White : DarkControlPalette.TextMuted,
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.HorizontalCenter);
        }
    }
}
