using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>What happened, so the toast can say it in one glyph instead of making
/// "Saved" and "Save failed" look identical.</summary>
public enum ToastSeverity
{
    Info,
    Success,
    Error,
}

/// <summary>
/// The app's one in-place confirmation HUD: a severity glyph, a title, a body line and an
/// optional action, shown near whatever it is confirming — CleanShot-style instant feedback
/// instead of the slow, Focus-Assist-throttled Windows tray balloon. Auto-dismisses. Fades
/// and rises in, and reverses on the way out, so it never pops or vanishes.
/// </summary>
public sealed class ToastWindow : WF.Form
{
    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private readonly WF.Timer _dismiss = new();
    private readonly SD.Point? _anchor;

    /// <summary>Entrance/exit: fade plus a short rise. A toast is a surface arriving on
    /// screen, which is what EnterDurationMs names; scale is deliberately not used — the
    /// toast's children sit at fixed offsets, so resizing the form would crop them rather
    /// than scale them.</summary>
    private const int RiseMs = Motion.EnterDurationMs;
    private const double RestOpacity = 0.97;

    /// <summary>An error is the message the user most needs to finish reading, so it holds
    /// the screen as long as an action toast does instead of the 1.7 s a confirmation gets.</summary>
    private const int DwellMs = 1700;
    private const int LongDwellMs = 4500;

    /// <summary>One 13-DIP row of body text, and the most rows the toast will grow to.
    /// Trim measures against these rather than guessing a character count.</summary>
    private const int BodyLineH = 22;
    private const int BodyMaxLines = 3;

    /// <summary>Well past what three rows can show, and what bounds Trim's walk.</summary>
    private const int MaxChars = 512;

    /// <summary>The body column: the 300-DIP card less the glyph gutter and the right pad.</summary>
    internal const int BodyWidthDip = 300 - 42 - 14;

    private IDisposable? _motion;
    private bool _exiting;
    private SD.Point _restLocation;
    private string _body;
    private double _entranceAmount;
    private readonly WF.Label _glyphLabel;
    private readonly WF.Label _previewLabel;

    // Point fonts render at the monitor's DPI, so the fixed-pixel layout scales with
    // the anchor monitor or labels truncate on 125%/150% displays.
    private readonly double _scale;

    private int S(int logical) => WinShot.Recording.RecordingMonitorDpi.Px(logical, _scale);

    public ToastWindow(
        string title,
        string preview,
        SD.Point? anchorScreen,
        Action? onOpen,
        ToastSeverity severity = ToastSeverity.Info,
        bool holdLong = false)
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

        // The severity glyph is the only thing that separates "Saved" from "Save failed" at
        // a glance, so it gets its own colour as well as its own shape.
        _glyphLabel = Label(GlyphOf(severity), 14, 14, 22, ThemePalette.TextTitlePt, ColorOf(severity), height: 24);
        _glyphLabel.Font = ThemePalette.IconFont(ThemePalette.TextTitlePt);
        _glyphLabel.AccessibleRole = WF.AccessibleRole.Graphic;
        _glyphLabel.AccessibleName = severity.ToString();
        Controls.Add(_glyphLabel);

        const int textX = 42;
        int textW = width - textX - 14;

        // Accent stays reserved for actions; the title reads as text, not a control.
        // 12 pt Segoe UI Bold needs ~22 px of line box; the shared 22 px row left the
        // descenders on the clip edge, so the title gets its own (still clearing y=38).
        var titleLabel = Label(title, textX, 14, textW, ThemePalette.TextTitlePt, ThemePalette.TextPrimary, bold: true, height: 24);
        titleLabel.AccessibleName = title;
        titleLabel.AccessibleRole = WF.AccessibleRole.StaticText;
        Controls.Add(titleLabel);

        // The friendly-error lines are 45-77 characters, which is two to three of these
        // 244 px rows. A single ellipsed line cut off exactly the half that says what to do
        // about it ("Choose a different folder in Settings"), so the body wraps and the
        // window grows to whatever it needs, up to BodyMaxLines — which is also what Trim cuts to.
        _body = Trim(preview, textW);
        int bodyLines = LinesNeeded(_body, textW);
        int bodyH = BodyLineH + ((bodyLines - 1) * BodyLineH);
        _previewLabel = Label(_body, textX, 38, textW, ThemePalette.TextBodyLgPt, ThemePalette.TextSecondary, height: bodyH);
        _previewLabel.AutoEllipsis = true;
        _previewLabel.UseMnemonic = false;
        _previewLabel.TextAlign = bodyLines > 1 ? SD.ContentAlignment.TopLeft : SD.ContentAlignment.MiddleLeft;
        _previewLabel.AccessibleName = _body;
        _previewLabel.AccessibleRole = WF.AccessibleRole.StaticText;
        Controls.Add(_previewLabel);

        int bodyBottom = 38 + bodyH;
        ClientSize = new SD.Size(S(width), S(hasOpen ? bodyBottom + 8 + 26 + 10 : bodyBottom + 10));

        if (onOpen is not null)
        {
            var open = new DarkButton
            {
                Scale = _scale,
                BackColor = Back,
                FillColor = ThemePalette.Accent,
                Location = new SD.Point(S(width - 14 - 96), S(bodyBottom + 8)),
                Size = new SD.Size(S(96), S(26)),
                Text = "Open link",
            };
            open.Click += (_, _) =>
            {
                try
                {
                    onOpen();
                    Close();
                }
                catch (Exception ex)
                {
                    // Closing on a failure is the success-but-no-failure-feedback pattern:
                    // the toast vanished and nothing opened. Say what happened on the card
                    // that is already on screen, and hold it there long enough to read.
                    Log.Error("Toast action failed", ex);
                    ShowActionFailure(FriendlyError.For(ex));
                }
            };
            Controls.Add(open);
        }

        // FormBorderStyle.None with no Text leaves an unnamed window: Narrator announced the
        // tray balloon this replaced, so the toast has to name itself the same way.
        AccessibleRole = WF.AccessibleRole.Alert;
        AccessibleName = title;
        AccessibleDescription = _body;

        // holdLong is for a working state ("Checking for updates…") whose work outlives the
        // confirmation dwell: it must not self-dismiss before its own result arrives.
        _dismiss.Interval = hasOpen || holdLong || severity == ToastSeverity.Error ? LongDwellMs : DwellMs;
        _dismiss.Tick += (_, _) => Close();
        MouseEnter += (_, _) => HoldOpen();
        MouseLeave += (_, _) => _dismiss.Start();
        foreach (WF.Control c in Controls)
        {
            c.MouseEnter += (_, _) => HoldOpen();
            c.MouseLeave += (_, _) => _dismiss.Start();
        }
        KeyDown += (_, e) => { if (e.KeyCode == WF.Keys.Escape) Close(); };
    }

    /// <summary>
    /// How many body rows <paramref name="text"/> needs inside a <paramref name="availableDip"/>-wide
    /// column, capped at <see cref="BodyMaxLines"/>. The layout is in DIP and only S() turns it
    /// into device pixels, so this number has to come out the same on every monitor.
    /// </summary>
    internal static int LinesNeeded(string text, int availableDip) =>
        Math.Clamp(RowsFor(text, availableDip), 1, BodyMaxLines);

    /// <summary>
    /// The uncapped row count. A pixel-unit font is not DPI-free: GDI+ still converts it
    /// through the screen DC, so the probe is 13 px em at 96 DPI and 19.5 px on Isaiah's 150 %
    /// primary. Measuring a DIP-wide column against that wrapped a row early and parked every
    /// wrapped toast on the BodyMaxLines clamp — 43 px of dead space under a two-line message,
    /// and clipping for anything that genuinely needed three. Scaling the column by the same
    /// DPI the font was scaled by makes the two cancel.
    /// </summary>
    private static int RowsFor(string text, int availableDip)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        try
        {
            using SD.Font ui = ThemePalette.UiFont(ThemePalette.TextBodyLgPt);
            using var dip = new SD.Font(ui.FontFamily, ThemePalette.TextBodyLgPt * 96f / 72f, SD.GraphicsUnit.Pixel);
            const WF.TextFormatFlags flags = WF.TextFormatFlags.WordBreak | WF.TextFormatFlags.NoPrefix;

            float dpi;
            using (var screen = SD.Graphics.FromHwnd(IntPtr.Zero))
                dpi = screen.DpiX > 0 ? screen.DpiX : 96f;
            var column = new SD.Size(Math.Max(1, (int)Math.Round(availableDip * dpi / 96f)), int.MaxValue);

            // MeasureText adds a constant padding to the height on top of n line boxes, so
            // dividing by the font's line height over-counts. Two references give both terms.
            int one = WF.TextRenderer.MeasureText("Mg", dip, column, flags).Height;
            int two = WF.TextRenderer.MeasureText("Mg\nMg", dip, column, flags).Height;
            int lineH = Math.Max(1, two - one);
            int padding = one - lineH;

            SD.Size measured = WF.TextRenderer.MeasureText(text, dip, column, flags);
            int rows = Math.Max(1, (int)Math.Round((measured.Height - padding) / (double)lineH));

            // WordBreak cannot break inside a word, so one long space-free token (a saved
            // file name) measures as a single row wider than the column and then renders
            // clipped at the label edge with no trim applied. Charge it what it really costs,
            // so LinesNeeded and Trim agree about it.
            if (measured.Width > column.Width)
                rows = Math.Max(rows, (int)Math.Ceiling(measured.Width / (double)column.Width));
            return rows;
        }
        catch
        {
            // No font, no measurement — one row is what the toast always used to give.
            return 1;
        }
    }

    /// <summary>Segoe MDL2 / Fluent codepoints that exist in both fonts.</summary>
    private static string GlyphOf(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => "",   // CheckMark
        ToastSeverity.Error => "",     // Error
        _ => "",                       // Info
    };

    private static SD.Color ColorOf(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => ThemePalette.Accent,
        ToastSeverity.Error => ThemePalette.Red,
        _ => ThemePalette.TextSecondary,
    };

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
        Announce();
    }

    /// <summary>
    /// The toast never takes focus, so nothing about it reaches a screen reader on its own.
    /// The tray balloon it replaced was spoken; one UIA notification restores that.
    /// </summary>
    private void Announce()
    {
        try
        {
            AccessibilityObject.RaiseAutomationNotification(
                WF.Automation.AutomationNotificationKind.Other,
                WF.Automation.AutomationNotificationProcessing.MostRecent,
                string.IsNullOrEmpty(_body) ? AccessibleName ?? string.Empty : $"{AccessibleName}. {_body}");
        }
        catch
        {
            // UI Automation is optional; the visible toast remains the fallback.
        }
    }

    /// <summary>
    /// The action failed. The toast becomes its own error report: the glyph and the body say
    /// so in place, rather than the card disappearing with nothing to show for the click.
    /// </summary>
    private void ShowActionFailure(string message)
    {
        if (IsDisposed) return;
        _dismiss.Stop();
        _body = Trim(message);
        _previewLabel.Text = _body;
        _previewLabel.AccessibleName = _body;
        _previewLabel.ForeColor = ThemePalette.TextError;
        _glyphLabel.Text = GlyphOf(ToastSeverity.Error);
        _glyphLabel.ForeColor = ColorOf(ToastSeverity.Error);
        _glyphLabel.AccessibleName = nameof(ToastSeverity.Error);
        AccessibleDescription = _body;
        _dismiss.Interval = LongDwellMs;
        _dismiss.Start();
        Announce();
    }

    /// <summary>
    /// Hovering holds the toast open — including after the dismiss timer already fired.
    /// Without the second half the user reaching for "Open link" watches the button slide
    /// away from under the cursor while it is still hit-testable.
    /// </summary>
    private void HoldOpen()
    {
        _dismiss.Stop();
        if (!_exiting) return;

        _exiting = false;
        _motion?.Dispose();
        // Resume from wherever the fade-out had reached — the mirror of OnFormClosing. A fresh
        // run from 0 dropped a half-faded toast to transparent for a frame and climbed back,
        // which flashed on exactly the gesture this method exists to serve.
        double from = _entranceAmount;
        _motion = Motion.Run(RiseMs, Motion.EaseOutCubic, p => ApplyEntrance(from + ((1d - from) * p)));
    }

    /// <summary>0 = fully out (transparent, dropped by the rise distance), 1 = at rest.</summary>
    private void ApplyEntrance(double amount)
    {
        if (IsDisposed || !IsHandleCreated) return;
        _entranceAmount = Math.Clamp(amount, 0d, 1d);
        Opacity = RestOpacity * Math.Clamp(amount, 0d, 1d);
        Location = new SD.Point(_restLocation.X, _restLocation.Y + (int)Math.Round(S(8) * (1d - amount)));
    }

    protected override void OnFormClosing(WF.FormClosingEventArgs e)
    {
        // Entrance and exit are the same move in reverse; without this the toast
        // disappeared at full opacity the instant the dismiss timer fired. Windows shutting
        // down is the exception: nothing is left to watch the fade and the pump may already
        // be gone, so a cancelled close there would never come back.
        bool ordinaryClose =
            e.CloseReason is WF.CloseReason.None or WF.CloseReason.UserClosing or WF.CloseReason.FormOwnerClosing;
        if (!_exiting && ordinaryClose && IsHandleCreated && !IsDisposed && Motion.AnimationsEnabled())
        {
            e.Cancel = true;
            _exiting = true;
            _dismiss.Stop();
            _motion?.Dispose();
            // From wherever the entrance had reached, not from full opacity: a toast replaced
            // 60 ms in used to snap to rest before fading.
            double from = _entranceAmount;
            _motion = Motion.Run(RiseMs, Motion.EaseOutCubic, p => ApplyEntrance(from * (1d - p)), CloseIfStillExiting);
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>The exit tween's onDone. A hover during the fade-out cancels the exit and
    /// runs the entrance forward again, so the close must not land afterwards.</summary>
    private void CloseIfStillExiting()
    {
        if (_exiting) Close();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    /// <summary>
    /// Disposal, not just Close(), releases the tween — a caller that disposes the toast
    /// mid-entrance never runs OnClosed, and the orphaned tween would hold the process-wide
    /// high-resolution clock until it finished on its own.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dismiss.Stop();
            _dismiss.Dispose();
            _motion?.Dispose();
            _motion = null;
        }
        base.Dispose(disposing);
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
            // Nothing better to anchor to: the bottom-right corner of the monitor the user is
            // working on (the pointer's), which is where the balloon used to appear on a
            // single-monitor PC and is closer to the eye than the primary's tray on a stack.
            x = area.Right - Width - 16;
            y = area.Bottom - Height - 16;
        }
        Location = new SD.Point(x, y);
    }

    /// <summary>
    /// Flattens the message to one paragraph and cuts it to what the body box can actually
    /// show. The cap used to be a flat 90 characters, which sat *inside* the three-row box and
    /// chopped the "no OCR language pack" line at "Time &amp; Language…", throwing away the half
    /// that names the Settings page — the only half that message exists for. The box knows its
    /// own capacity, so ask it, and cut on a word so what survives is a whole phrase.
    /// </summary>
    internal static string Trim(string text, int availableDip = BodyWidthDip)
    {
        text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0) return text;

        // The walk below re-measures the whole remainder on every pass, so its cost is
        // quadratic in the length: 2 000 characters already cost a second on the UI thread.
        // Three rows hold well under 200 characters at any DPI, so cutting to a ceiling
        // first is free for every real message and bounds the walk for anything else.
        bool capped = text.Length > MaxChars;
        if (capped) text = text[..MaxChars];
        if (!capped && RowsFor(text, availableDip) <= BodyMaxLines)
            return text;

        string cut = text;
        while (cut.Length > 1 && RowsFor(cut + "…", availableDip) > BodyMaxLines)
        {
            int space = cut.LastIndexOf(' ', cut.Length - 2);
            cut = (space > 0 ? cut[..space] : cut[..(cut.Length - 1)]).TrimEnd();
        }
        return cut + "…";
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
