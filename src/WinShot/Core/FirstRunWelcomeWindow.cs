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
    private readonly List<RowHit> _rows = [];
    private readonly Action _onDismissed;
    private int _hoverRow = -1;
    private IDisposable? _entrance;
    private double _entranceAmount;
    private SD.Point _restLocation;
    private bool _tailOnTop;

    private sealed record RowHit(SD.Rectangle Bounds, WelcomeAction Action);

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

        var dismiss = new DarkButton
        {
            Scale = _scale,
            BackColor = ThemePalette.ToolbarBg,
            FillColor = ThemePalette.Accent,
            Size = new SD.Size(S(78), S(26)),
            Text = "Got it",
        };
        dismiss.Click += (_, _) => Close();
        Controls.Add(dismiss);
        _dismissButton = dismiss;

        MouseMove += OnBubbleMouseMove;
        MouseLeave += (_, _) => SetHoverRow(-1);
        MouseClick += OnBubbleMouseClick;
    }

    private readonly DarkButton _dismissButton;

    public IReadOnlyList<WelcomeAction> Actions { get; }

    /// <summary>Rows as laid out, so a test can assert the hit areas without a screen.</summary>
    internal IReadOnlyList<SD.Rectangle> RowBoundsForTest => _rows.Select(r => r.Bounds).ToList();

    protected override bool ShowWithoutActivation => true;

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
        _entranceAmount = amount;
        Opacity = Math.Clamp(amount, 0, 1) * 0.98;
        int rise = S(10);
        int offset = (int)Math.Round((1 - Math.Clamp(amount, 0, 1)) * rise);
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
        _rows.Clear();
        SD.Rectangle body = BodyBounds;
        int y = body.Top + S(HeaderHeightDip);
        foreach (WelcomeAction action in Actions)
        {
            var bounds = new SD.Rectangle(S(PadDip / 2), y, body.Width - S(PadDip), S(RowHeightDip));
            _rows.Add(new RowHit(bounds, action));
            y += S(RowHeightDip);
        }
        _dismissButton.Location = new SD.Point(
            body.Right - S(PadDip) - _dismissButton.Width,
            body.Bottom - S(FooterHeightDip) + S(8));
    }

    private void OnBubbleMouseMove(object? sender, WF.MouseEventArgs e)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Bounds.Contains(e.Location))
            {
                SetHoverRow(i);
                return;
            }
        }
        SetHoverRow(-1);
    }

    private void SetHoverRow(int index)
    {
        if (_hoverRow == index) return;
        _hoverRow = index;
        Cursor = index >= 0 ? WF.Cursors.Hand : WF.Cursors.Default;
        Invalidate();
    }

    private void OnBubbleMouseClick(object? sender, WF.MouseEventArgs e)
    {
        foreach (RowHit row in _rows)
        {
            if (!row.Bounds.Contains(e.Location)) continue;
            // The row runs the command. Closing first means the capture overlay does not open
            // underneath a topmost bubble.
            Close();
            try { row.Action.Run(); }
            catch (Exception ex) { Log.Error($"First-run action '{row.Action.Title}' failed", ex); }
            return;
        }
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ThemePalette.ToolbarBg);

        SD.Rectangle body = BodyBounds;

        // Rows first, so the border and the tail draw over their edges.
        for (int i = 0; i < _rows.Count; i++)
        {
            RowHit row = _rows[i];
            if (i == _hoverRow)
            {
                using var hover = new SD.SolidBrush(ThemePalette.HoverFill);
                using var path = GdiPaths.RoundedRect(row.Bounds, S(8));
                g.FillPath(hover, path);
            }

            using var glyphFont = ThemePalette.IconFont(ThemePalette.TextTitlePt);
            using var glyphBrush = new SD.SolidBrush(ThemePalette.Accent);
            g.DrawString(
                row.Action.Glyph, glyphFont, glyphBrush,
                new SD.RectangleF(row.Bounds.X + S(10), row.Bounds.Y + S(12), S(24), S(24)));

            WF.TextRenderer.DrawText(
                g, row.Action.Title, ThemePalette.UiFont(ThemePalette.TextBodyLgPt, SD.FontStyle.Bold),
                new SD.Rectangle(row.Bounds.X + S(42), row.Bounds.Y + S(6), row.Bounds.Width - S(52), S(20)),
                ThemePalette.TextPrimary,
                WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.EndEllipsis);

            WF.TextRenderer.DrawText(
                g, row.Action.Hint, ThemePalette.UiFont(ThemePalette.TextCaptionPt),
                new SD.Rectangle(row.Bounds.X + S(42), row.Bounds.Y + S(24), row.Bounds.Width - S(52), S(18)),
                ThemePalette.TextSecondary,
                WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.EndEllipsis);
        }

        WF.TextRenderer.DrawText(
            g, "WinShot is ready", ThemePalette.UiFont(ThemePalette.TextTitlePt, SD.FontStyle.Bold),
            new SD.Rectangle(S(PadDip), body.Top + S(12), body.Width - S(PadDip * 2), S(24)),
            ThemePalette.TextPrimary,
            WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter);

        WF.TextRenderer.DrawText(
            g, "Try one now, or find everything in the tray icon.",
            ThemePalette.UiFont(ThemePalette.TextCaptionPt),
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
        }
        base.Dispose(disposing);
    }
}
