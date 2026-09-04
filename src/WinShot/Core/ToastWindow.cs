
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>
/// The app's one in-place confirmation HUD: a title, a body line and an optional action,
/// shown near whatever it is confirming — CleanShot-style instant feedback instead of the
/// slow, Focus-Assist-throttled Windows tray balloon. Auto-dismisses. Fades and rises in,
/// and reverses on the way out, so it never pops or vanishes.
/// </summary>
public sealed class ToastWindow : WF.Form
{
    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private readonly WF.Timer _dismiss = new();
    private readonly SD.Point? _anchor;

    /// <summary>Entrance/exit: fade plus a short rise, on the shared glide step rather than
    /// a number of its own. Scale is deliberately not used — the toast's children sit at
    /// fixed offsets, so resizing the form would crop them rather than scale them.</summary>
    private const int RiseMs = Motion.GlideDurationMs;
    private const double RestOpacity = 0.97;

    private IDisposable? _motion;
    private bool _exiting;
    private SD.Point _restLocation;

    // Point fonts render at the monitor's DPI, so the fixed-pixel layout scales with
    // the anchor monitor or labels truncate on 125%/150% displays.
    private readonly double _scale;

    private int S(int logical) => (int)Math.Round(logical * _scale);

    public ToastWindow(string title, string preview, SD.Point? anchorScreen, Action? onOpen)
    {
        _anchor = anchorScreen;
        _scale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(
            WF.Screen.FromPoint(anchorScreen ?? WF.Cursor.Position).Bounds);

        // Born on the anchor's monitor. Left at the default the form is created on the
        // primary, so a mixed-DPI move fires WM_DPICHANGED and re-renders the point-sized
        // fonts at a DPI the manual _scale layout was never computed for.
        Location = WF.Screen.FromPoint(anchorScreen ?? WF.Cursor.Position).Bounds.Location;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Opacity = 0;            // OnShown fades it up to RestOpacity
        Padding = new WF.Padding(S(14));
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        const int width = 300;
        bool hasOpen = onOpen is not null;
        int height = hasOpen ? 104 : 70;
        ClientSize = new SD.Size(S(width), S(height));

        // Accent stays reserved for actions; the title reads as text, not a control.
        // 12 pt Segoe UI Bold needs ~22 px of line box; the shared 22 px row left the
        // descenders on the clip edge, so the title gets its own (still clearing y=38).
        var titleLabel = Label(title, 14, 14, width - 28, ThemePalette.TextTitlePt, ThemePalette.TextPrimary, bold: true, height: 24);
        Controls.Add(titleLabel);

        var previewLabel = Label(Trim(preview), 14, 38, width - 28, ThemePalette.TextBodyLgPt, ThemePalette.TextSecondary);
        previewLabel.AutoEllipsis = true;
        previewLabel.UseMnemonic = false;
        Controls.Add(previewLabel);

        if (onOpen is not null)
        {
            var open = new DarkButton
            {
                Scale = _scale,
                BackColor = Back,
                FillColor = ThemePalette.Accent,
                Location = new SD.Point(S(width - 14 - 96), S(68)),
                Size = new SD.Size(S(96), S(26)),
                Text = "Open link",
            };
            open.Click += (_, _) => { try { onOpen(); } catch (Exception ex) { Log.Error("OCR toast open failed", ex); } Close(); };
            Controls.Add(open);
        }

        _dismiss.Interval = hasOpen ? 4500 : 1700;
        _dismiss.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _dismiss.Stop();
        MouseLeave += (_, _) => _dismiss.Start();
        foreach (WF.Control c in Controls)
        {
            c.MouseEnter += (_, _) => _dismiss.Stop();
            c.MouseLeave += (_, _) => _dismiss.Start();
        }
        KeyDown += (_, e) => { if (e.KeyCode == WF.Keys.Escape) Close(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateRegion();
        Position();
        _restLocation = Location;
        _motion?.Dispose();
        _motion = Motion.Run(RiseMs, Motion.EaseOutCubic, ApplyEntrance);
        _dismiss.Start();
    }

    /// <summary>0 = fully out (transparent, dropped by the rise distance), 1 = at rest.</summary>
    private void ApplyEntrance(double amount)
    {
        if (IsDisposed || !IsHandleCreated) return;
        Opacity = RestOpacity * Math.Clamp(amount, 0d, 1d);
        Location = new SD.Point(_restLocation.X, _restLocation.Y + (int)Math.Round(S(8) * (1d - amount)));
    }

    protected override void OnFormClosing(WF.FormClosingEventArgs e)
    {
        // Entrance and exit are the same move in reverse; without this the toast
        // disappeared at full opacity the instant the dismiss timer fired.
        if (!_exiting && IsHandleCreated && !IsDisposed && Motion.AnimationsEnabled())
        {
            e.Cancel = true;
            _exiting = true;
            _dismiss.Stop();
            _motion?.Dispose();
            _motion = Motion.Run(RiseMs, Motion.EaseOutCubic, p => ApplyEntrance(1d - p), Close);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismiss.Stop();
        _dismiss.Dispose();
        _motion?.Dispose();
        _motion = null;
        base.OnClosed(e);
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

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        PopupChrome.DrawBorder(e.Graphics, ClientSize, S(14));
    }

    private void Position()
    {
        SD.Rectangle area = WF.Screen.FromPoint(_anchor ?? WF.Cursor.Position).WorkingArea;
        int x, y;
        if (_anchor is SD.Point a)
        {
            // Centered above the selection; flip below if there isn't room.
            x = a.X - Width / 2;
            y = a.Y - Height - 16;
            x = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
            if (y < area.Top + 8) y = a.Y + 16;
            y = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
        }
        else
        {
            x = area.Right - Width - 16;
            y = area.Bottom - Height - 16;
        }
        Location = new SD.Point(x, y);
    }

    private static string Trim(string text)
    {
        text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 90 ? text[..90] + "…" : text;
    }

    private void UpdateRegion() => PopupChrome.ApplyRegion(this, S(14));

    private WF.Label Label(string text, int x, int y, int width, float size, SD.Color color, bool bold = false, int height = 22) =>
        new()
        {
            AutoSize = false,
            BackColor = SD.Color.Transparent,
            Font = ThemePalette.UiFont(size, bold ? SD.FontStyle.Bold : SD.FontStyle.Regular),
            ForeColor = color,
            Location = new SD.Point(S(x), S(y)),
            Size = new SD.Size(S(width), S(height)),
            Text = text,
            TextAlign = SD.ContentAlignment.MiddleLeft,
        };
}
