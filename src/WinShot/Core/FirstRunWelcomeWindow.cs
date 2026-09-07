using System.Drawing.Drawing2D;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>
/// What a new user sees on their first launch. Before this, WinShot installed, put a 16 px
/// icon in the tray, and told nobody that eight hotkeys were already bound.
///
/// One surface, not two: a pointer bubble anchored under the tray icon — the same anchored,
/// DPI-scaled, drop-shadowed chrome the toast uses, plus a triangle tail — whose three rows
/// <b>run</b> the commands rather than describing them, so the first capture happens inside
/// onboarding. Two stacked first-launch popups (a bubble and then a card) would be noise on
/// a surface whose entire job is to not be noise.
///
/// Non-modal, dismissible, and shown exactly once: <see cref="Settings.HasCompletedFirstRun"/>
/// is set the moment it appears, so a crash mid-onboarding does not re-teach the user.
/// </summary>
public sealed class FirstRunWelcomeWindow : WF.Form
{
    /// <summary>A row that runs something. The label is the promise; the command is the act.</summary>
    public sealed record WelcomeAction(string Glyph, string Title, string Hint, Action Run);

    private const int CardWidthDip = 320;
    private const int RowHeightDip = 46;
    private const int HeaderHeightDip = 62;
    private const int FooterHeightDip = 42;
    private const int PadDip = 14;
    private const int TailWidthDip = 18;
    private const int TailHeightDip = 9;
    private const int CornerDip = 14;

    private readonly double _scale;
    private readonly WF.Screen _screen;
    private readonly SD.Point? _anchor;
    private readonly List<WelcomeRow> _rows = [];
    private readonly Action _onDismissed;
    private readonly SD.Font _titleFont;
    private readonly SD.Font _subtitleFont;
    private IDisposable? _entrance;
    private SD.Point _restLocation;
    private bool _tailOnTop;

    private int S(int logical) => Recording.RecordingMonitorDpi.Px(logical, _scale);

    /// <param name="anchorScreen">A point on the tray icon, in screen pixels. The bubble sits
    /// above it with the tail pointing down at it; null falls back to the pointer's monitor.</param>
    public FirstRunWelcomeWindow(
        SD.Point? anchorScreen,
        IReadOnlyList<WelcomeAction> actions,
        Action onDismissed)
    {
        _anchor = anchorScreen;
        _onDismissed = onDismissed;
        _screen = WF.Screen.FromPoint(anchorScreen ?? WF.Cursor.Position);
        _scale = Recording.RecordingMonitorDpi.ScaleFor(_screen.Bounds);
        Location = _screen.Bounds.Location; // born on the anchor's monitor; see ToastWindow

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = ThemePalette.ToolbarBg;
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        Opacity = 0;
        DoubleBuffered = true;
        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        AccessibleRole = WF.AccessibleRole.Alert;
        AccessibleName = "WinShot is ready";
        AccessibleDescription = "Pick a capture to try, or dismiss this.";

        Actions = actions;
        ClientSize = new SD.Size(
            S(CardWidthDip),
            S(HeaderHeightDip + RowHeightDip * actions.Count + FooterHeightDip) + S(TailHeightDip));

        _titleFont = ThemePalette.UiFont(ThemePalette.TextTitlePt, SD.FontStyle.Bold);
        _subtitleFont = ThemePalette.UiFont(ThemePalette.TextCaptionPt);

        // Real controls, not painted rectangles with a hit test. The rows are the whole point
        // of WinShot's one onboarding surface, and as paint they had no tab stop, no
        // accessible children and no way to be run without a mouse.
        foreach (WelcomeAction action in actions)
        {
            var row = new WelcomeRow(action, _scale, Run);
            _rows.Add(row);
            Controls.Add(row);
        }

        var dismiss = new DarkButton
        {
            Scale = _scale,
            BackColor = ThemePalette.ToolbarBg,
            // AccentDeep: DarkButton puts a white label on a saturated fill, and white on
            // the raw accent is 3.65:1 — under the text floor, on the app's first screen.
            FillColor = ThemePalette.AccentDeep,
            Size = new SD.Size(S(78), S(26)),
            Text = "Got it",
            TabStop = true,
        };
        dismiss.AccessibleName = "Got it";
        dismiss.AccessibleDescription = "Close this and get on with it.";
        dismiss.Click += (_, _) => Close();
        Controls.Add(dismiss);
        _dismissButton = dismiss;
    }

    private readonly DarkButton _dismissButton;

    /// <summary>Runs a row's command. Closing FIRST is deliberate, and is also why there is
    /// no exit tween: the command opens a capture overlay in this same call, and a bubble
    /// that spent 280 ms fading would spend it on top of the overlay it just launched.
    /// </summary>
    private void Run(WelcomeAction action)
    {
        Close();
        try { action.Run(); }
        catch (Exception ex) { Log.Error($"First-run action '{action.Title}' failed", ex); }
    }

    public IReadOnlyList<WelcomeAction> Actions { get; }

    /// <summary>Rows as laid out, so a test can assert the hit areas without a screen.</summary>
    internal IReadOnlyList<SD.Rectangle> RowBoundsForTest => _rows.Select(r => r.Bounds).ToList();

    /// <summary>
    /// The bubble takes focus. It used to refuse activation, which meant its rows and its
    /// "Got it" button could never be reached from the keyboard and Escape went to whatever
    /// was underneath - a topmost onboarding surface a keyboard-only user could neither run
    /// nor close. There is nothing worth protecting from focus theft on a first launch.
    /// </summary>
    protected override bool ShowWithoutActivation => false;

    protected override bool ProcessCmdKey(ref WF.Message msg, WF.Keys keyData)
    {
        if (keyData == WF.Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW, matching the toast and the card
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Position();
        LayoutRows();
        _restLocation = Location;
        _entrance?.Dispose();
        // Arriving chrome, with the same overshoot the quick-actions card uses: this is the
        // one surface whose whole job is to be noticed.
        _entrance = Motion.Run(Motion.EnterDurationMs, Motion.EaseOutBack, ApplyEntrance);
    }

    private void ApplyEntrance(double amount)
    {
        // Opacity is the one property that HAS to be clamped - Form.Opacity throws outside
        // 0..1. The rise is not: EaseOutBack passes 1.0 about halfway through, and letting
        // the offset go negative is what makes the bubble overshoot the anchor and settle
        // back. Clamping both meant the tween reached its rest pose at 140 ms and then sat
        // still for the other 140, which is the overshoot paid for and thrown away.
        Opacity = Math.Clamp(amount, 0, 1) * 0.98;
        int rise = S(10);
        int offset = (int)Math.Round((1 - amount) * rise);
        Location = new SD.Point(_restLocation.X, _restLocation.Y + (_tailOnTop ? -offset : offset));
    }

    private void Position()
    {
        SD.Rectangle area = _screen.WorkingArea;
        int gap = S(8);
        int x, y;
        if (_anchor is SD.Point a)
        {
            x = a.X - Width / 2;
            y = a.Y - Height - gap;
            _tailOnTop = false;
            if (y < area.Top + gap)
            {
                // No room above the anchor (a top-docked taskbar): flip and point up.
                y = a.Y + gap;
                _tailOnTop = true;
            }
            x = Math.Clamp(x, area.Left + gap, Math.Max(area.Left + gap, area.Right - Width - gap));
            y = Math.Clamp(y, area.Top + gap, Math.Max(area.Top + gap, area.Bottom - Height - gap));
            _tailCenterX = Math.Clamp(a.X - x, S(CornerDip) + S(TailWidthDip), Width - S(CornerDip) - S(TailWidthDip));
        }
        else
        {
            x = area.Right - Width - S(24);
            y = area.Bottom - Height - S(24);
            _tailOnTop = false;
            _tailCenterX = Width - S(48);
        }
        Location = new SD.Point(x, y);
        UpdateRegion();
    }

    private int _tailCenterX;

    /// <summary>The body box, i.e. everything but the tail strip.</summary>
    private SD.Rectangle BodyBounds => _tailOnTop
        ? new SD.Rectangle(0, S(TailHeightDip), ClientSize.Width, ClientSize.Height - S(TailHeightDip))
        : new SD.Rectangle(0, 0, ClientSize.Width, ClientSize.Height - S(TailHeightDip));

    private void LayoutRows()
    {
        SD.Rectangle body = BodyBounds;
        int y = body.Top + S(HeaderHeightDip);
        foreach (WelcomeRow row in _rows)
        {
            row.Bounds = new SD.Rectangle(S(PadDip / 2), y, body.Width - S(PadDip), S(RowHeightDip));
            y += S(RowHeightDip);
        }
        _dismissButton.Location = new SD.Point(
            body.Right - S(PadDip) - _dismissButton.Width,
            body.Bottom - S(FooterHeightDip) + S(8));
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ThemePalette.ToolbarBg);

        SD.Rectangle body = BodyBounds;

        WF.TextRenderer.DrawText(
            g, "WinShot is ready", _titleFont,
            new SD.Rectangle(S(PadDip), body.Top + S(12), body.Width - S(PadDip * 2), S(24)),
            ThemePalette.TextPrimary,
            WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter);

        WF.TextRenderer.DrawText(
            g, "Try one now, or find everything in the tray icon.",
            _subtitleFont,
            new SD.Rectangle(S(PadDip), body.Top + S(34), body.Width - S(PadDip * 2), S(18)),
            ThemePalette.TextSecondary,
            WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter);

        PopupChrome.DrawBorder(g, ClientSize, S(CornerDip));
    }

    /// <summary>
    /// The rounded body plus a triangular tail pointing at the tray icon. The whole thing is
    /// one region so the drop shadow and the border follow the tail instead of cutting it off.
    /// </summary>
    private void UpdateRegion()
    {
        SD.Rectangle body = BodyBounds;
        int r = S(CornerDip);
        int tw = S(TailWidthDip);
        int th = S(TailHeightDip);

        using var path = new GraphicsPath();
        path.AddPath(GdiPaths.RoundedRect(body, r), false);

        int cx = Math.Clamp(_tailCenterX, r + tw, Math.Max(r + tw, ClientSize.Width - r - tw));
        if (_tailOnTop)
        {
            path.AddPolygon(new[]
            {
                new SD.Point(cx - tw / 2, body.Top),
                new SD.Point(cx, body.Top - th),
                new SD.Point(cx + tw / 2, body.Top),
            });
        }
        else
        {
            path.AddPolygon(new[]
            {
                new SD.Point(cx - tw / 2, body.Bottom),
                new SD.Point(cx, body.Bottom + th),
                new SD.Point(cx + tw / 2, body.Bottom),
            });
        }
        Region?.Dispose();
        Region = new SD.Region(path);
    }

    protected override void OnFormClosed(WF.FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        try { _onDismissed(); }
        catch (Exception ex) { Log.Error("First-run dismissal handler failed", ex); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _entrance?.Dispose();
            _entrance = null;
            // ThemePalette.UiFont hands back a NEW font every call, so these used to be
            // allocated per paint - four of them on every hover change - and never freed.
            _titleFont.Dispose();
            _subtitleFont.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// One onboarding row, as a real control: it takes a tab stop, it takes focus, Windows
    /// gives it an <see cref="WF.AccessibleObject"/> a screen reader can find, and Enter or
    /// Space runs it. Its wash cross-fades on <see cref="Motion.QuickMs"/> like every other
    /// hover in the app instead of snapping on an Invalidate.
    /// </summary>
    private sealed class WelcomeRow : WF.Control
    {
        private readonly WelcomeAction _action;
        private readonly double _scale;
        private readonly Action<WelcomeAction> _invoke;
        private readonly StateAmount _wash;
        private readonly SD.Font _glyphFont;
        private readonly SD.Font _titleFont;
        private readonly SD.Font _hintFont;

        private int S(int logical) => Recording.RecordingMonitorDpi.Px(logical, _scale);

        public WelcomeRow(WelcomeAction action, double scale, Action<WelcomeAction> invoke)
        {
            _action = action;
            _scale = scale;
            _invoke = invoke;
            _wash = new StateAmount(this);
            _glyphFont = ThemePalette.IconFont(ThemePalette.TextTitlePt);
            _titleFont = ThemePalette.UiFont(ThemePalette.TextBodyLgPt, SD.FontStyle.Bold);
            _hintFont = ThemePalette.UiFont(ThemePalette.TextCaptionPt);

            SetStyle(
                WF.ControlStyles.AllPaintingInWmPaint |
                WF.ControlStyles.OptimizedDoubleBuffer |
                WF.ControlStyles.ResizeRedraw |
                WF.ControlStyles.UserPaint |
                WF.ControlStyles.Selectable,
                true);
            BackColor = ThemePalette.ToolbarBg;
            Cursor = WF.Cursors.Hand;
            TabStop = true;
            AccessibleRole = WF.AccessibleRole.PushButton;
            AccessibleName = action.Title;
            AccessibleDescription = action.Hint;

            MouseEnter += (_, _) => _wash.To(1);
            MouseLeave += (_, _) => _wash.To(Focused ? 1 : 0);
            GotFocus += (_, _) => _wash.To(1);
            LostFocus += (_, _) => _wash.To(0);
            Click += (_, _) => _invoke(_action);
        }

        // Enter and Space are what a row IS; without this the form's default button eats them.
        protected override bool IsInputKey(WF.Keys keyData) =>
            keyData is WF.Keys.Enter or WF.Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(WF.KeyEventArgs e)
        {
            if (e.KeyCode is WF.Keys.Enter or WF.Keys.Space)
            {
                e.Handled = true;
                _invoke(_action);
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(WF.MouseEventArgs e)
        {
            Focus();
            base.OnMouseDown(e);
        }

        protected override void OnPaint(WF.PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(ThemePalette.ToolbarBg);

            var box = new SD.Rectangle(0, 0, Width, Height);
            if (_wash.Value > 0.001)
            {
                SD.Color fill = ThemePalette.HoverFill;
                using var hover = new SD.SolidBrush(
                    SD.Color.FromArgb((int)Math.Round(fill.A * _wash.Value), fill));
                using var path = GdiPaths.RoundedRect(box, S(8));
                g.FillPath(hover, path);
            }

            if (Focused && ShowFocusCues)
            {
                using var ring = new SD.Pen(ThemePalette.AccentRing, 1f);
                using var path = GdiPaths.RoundedRect(
                    new SD.Rectangle(0, 0, Width - 1, Height - 1), S(8));
                g.DrawPath(ring, path);
            }

            using (var glyphBrush = new SD.SolidBrush(ThemePalette.Accent))
                g.DrawString(_action.Glyph, _glyphFont, glyphBrush, new SD.RectangleF(S(10), S(12), S(24), S(24)));

            WF.TextRenderer.DrawText(
                g, _action.Title, _titleFont,
                new SD.Rectangle(S(42), S(6), Width - S(52), S(20)),
                ThemePalette.TextPrimary,
                WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.EndEllipsis);

            WF.TextRenderer.DrawText(
                g, _action.Hint, _hintFont,
                new SD.Rectangle(S(42), S(24), Width - S(52), S(18)),
                ThemePalette.TextSecondary,
                WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _wash.Dispose();
                _glyphFont.Dispose();
                _titleFont.Dispose();
                _hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
