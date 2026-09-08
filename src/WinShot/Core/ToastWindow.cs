using System.Runtime.InteropServices;
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
/// The extra affordances a toast can carry under its body, for the results that are a
/// decision rather than a confirmation. All optional; a plain confirmation passes none.
/// </summary>
public sealed class ToastExtras
{
    /// <summary>A button that does something and leaves the toast up, relabelling itself
    /// with whatever the action returns -- "Cut breaks" becoming "Add breaks".</summary>
    public string? ToggleText { get; init; }

    /// <summary>Runs the toggle and returns the button's new label. Async because the work
    /// behind it is a clipboard write, and a discarded Task faults where nobody is looking --
    /// the button relabelled itself and announced a re-copy that never happened.</summary>
    public Func<Task<string>>? Toggle { get; init; }

    /// <summary>A secondary button that declines and closes -- the explicit "no" that makes
    /// the primary button a choice instead of the only thing on offer.</summary>
    public string? SecondaryText { get; init; }

    public Action? Secondary { get; init; }

    /// <summary>A checkbox under the buttons: the user's standing answer to whatever the
    /// toast keeps offering. Ticking it is the way to stop being asked.</summary>
    public string? CheckboxText { get; init; }

    public Action<bool>? CheckboxChanged { get; init; }

    /// <summary>Never self-dismiss. For a toast the user has to answer: a 4.5 s window is
    /// not an offer, it is a reflex test.</summary>
    public bool Sticky { get; init; }

    public bool HasAny =>
        (ToggleText is not null && Toggle is not null) ||
        SecondaryText is not null ||
        CheckboxText is not null;
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

    /// <summary>
    /// The toast currently on screen. One feedback channel means one toast — a second lands
    /// on top of the first, exactly, whether they share the corner or an anchor rect. The
    /// rule lives here rather than in the caller because there is more than one caller: the
    /// quick-actions card raises its own share failures, and routing them past App's field
    /// put two cards on the same spot. Closed, not disposed: Dispose() never raises
    /// OnFormClosing, which is where the exit tween lives, so the outgoing toast vanished at
    /// full opacity and left the corner empty for the 280 ms the replacement spent rising.
    /// That is the whole "Reading text" -> result handoff, and a designed sequence must not
    /// blink; the outgoing card sinks and fades under the arriving one instead.
    /// </summary>
    private static ToastWindow? _live;

    /// <summary>
    /// The window handle of the toast currently on screen, or zero. A full-screen overlay
    /// that re-raises itself over anything covering it (see
    /// <c>SelectorForeground.KeepOnTop</c>) has to know its own confirmations are not
    /// intruders — the toast is TopMost and hit-testable, so the pointer resting on it reads
    /// as "something covered the overlay" and the overlay buries the message it just raised.
    /// </summary>
    internal static IntPtr LiveHandle =>
        _live is { IsDisposed: false } toast && toast.IsHandleCreated ? toast.Handle : IntPtr.Zero;

    private readonly WF.Timer _dismiss = new();
    private readonly SD.Point? _anchor;

    /// <summary>Entrance/exit: fade plus a short rise. A toast is a surface arriving on
    /// screen, which is what EnterDurationMs names; scale is deliberately not used — the
    /// toast's children sit at fixed offsets, so resizing the form would crop them rather
    /// than scale them.
    ///
    /// Declared deviation from F6, whose Fix column reads "~180 ms opacity + 0.92→1.0 scale
    /// in/out": this is a rise instead of a scale, over the F2 vocabulary's EnterDurationMs
    /// (280) rather than an invented 180. The intent F6 was after — nothing pops, entrance
    /// and exit symmetric, one shared timing vocabulary — is met; the shape is not. Changing
    /// it means moving the scale onto a content panel the way ThemedMessageDialog.Enter
    /// does, which is a layout rewrite of the toast body, not a motion change.</summary>
    private const int RiseMs = Motion.EnterDurationMs;
    private const double RestOpacity = 0.97;

    /// <summary>An error is the message the user most needs to finish reading, so it holds
    /// the screen as long as an action toast does instead of the 1.7 s a confirmation gets.</summary>
    private const int DwellMs = 1700;
    private const int LongDwellMs = 4500;

    /// <summary>A sticky toast waits for an answer rather than counting one down -- but it is
    /// a TopMost window over everything the user owns, so "waits" must not mean "forever".
    /// This is a backstop, not a dwell: long enough that nobody reads it as a countdown,
    /// short enough that a toast nobody answers cannot become permanent screen furniture.</summary>
    private const int StickyBackstopMs = 30_000;

    /// <summary>One 13-DIP row of body text, and the most rows the toast will grow to.
    /// Trim measures against these rather than guessing a character count.</summary>
    private const int BodyLineH = 22;
    private const int BodyMaxLines = 3;

    /// <summary>Well past what three rows can show, and what bounds Trim's walk.</summary>
    private const int MaxChars = 512;

    /// <summary>The working-area inset the corner toast keeps, matching the one
    /// <see cref="Overlay.QuickAccessOverlayLayout.Place"/> gives the quick-actions card.</summary>
    private const int EdgeInsetDip = 16;

    /// <summary>The card, its glyph gutter and its right pad, in DIP.</summary>
    private const int CardWidthDip = 300;
    private const int TextXDip = 42;
    private const int BodyTopDip = 38;

    /// <summary>The body column: the 300-DIP card less the glyph gutter and the right pad.</summary>
    internal const int BodyWidthDip = CardWidthDip - TextXDip - 14;

    private IDisposable? _motion;
    private bool _exiting;
    private SD.Point _restLocation;
    private string _body;
    private double _entranceAmount;
    private readonly WF.Label _glyphLabel;
    private readonly WF.Label _previewLabel;
    private readonly DarkButton? _openButton;
    private readonly DarkButton? _secondaryButton;
    private readonly DarkButton? _toggleButton;
    private readonly DarkCheckBox? _suppressCheck;
    private readonly bool _sticky;
    /// <summary>The dwell a non-sticky toast counts down. Held so the sticky backstop can be
    /// substituted in one place instead of at every Start() site.</summary>
    private int _dwellMs;
    private IntPtr _keyHook;
    private HookProc? _keyHookProc;

    /// <summary>
    /// The body label's real width in device pixels. Row counting has to measure against
    /// this and nothing else: it is the box the text actually lands in.
    /// </summary>
    private readonly int _bodyColumnPx;

    /// <summary>The column the toast measures against — what tests must pass to
    /// <see cref="LinesNeeded"/> and <see cref="Trim"/> to ask about this toast's box.</summary>
    internal int BodyColumnPx => _bodyColumnPx;

    /// <summary>How long this toast will dwell before dismissing itself, in ms. Exposed so a
    /// test can prove that a toast carrying a control gets the long window rather than the
    /// 1.7 s a plain confirmation gets.</summary>
    internal int DismissIntervalMs => _dismiss.Interval;

    // Point fonts render at the monitor's DPI, so the fixed-pixel layout scales with
    // the anchor monitor or labels truncate on 125%/150% displays.
    private readonly double _scale;

    /// <summary>The monitor this toast was laid out for; see the constructor.</summary>
    private readonly WF.Screen _screen;

    private int S(int logical) => WinShot.Recording.RecordingMonitorDpi.Px(logical, _scale);

    public ToastWindow(
        string title,
        string preview,
        SD.Point? anchorScreen,
        Action? onOpen,
        ToastSeverity severity = ToastSeverity.Info,
        bool holdLong = false,
        ToastExtras? extras = null)
    {
        _anchor = anchorScreen;
        _sticky = extras?.Sticky == true;

        // One screen read for the life of the toast. With no anchor the fallback is the
        // pointer's monitor, and the pointer can cross to a different-DPI display between
        // construction and Show() — re-reading it in Position() would lay the card out at
        // 150 % metrics and then place it on a 100 % display.
        _screen = WF.Screen.FromPoint(anchorScreen ?? WF.Cursor.Position);
        _scale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(_screen.Bounds);

        // Born on the anchor's monitor. Left at the default the form is created on the
        // primary, so a mixed-DPI move fires WM_DPICHANGED and re-renders the point-sized
        // fonts at a DPI the manual _scale layout was never computed for.
        Location = _screen.Bounds.Location;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        FormBorderStyle = WF.FormBorderStyle.None;
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

        // The severity glyph is the only thing that separates "Saved" from "Save failed" at
        // a glance, so it gets its own colour as well as its own shape.
        _glyphLabel = Label(GlyphOf(severity), 14, 14, 22, ThemePalette.TextTitlePt, ColorOf(severity), height: 24);
        _glyphLabel.Font = ThemePalette.IconFont(ThemePalette.TextTitlePt);
        _glyphLabel.AccessibleRole = WF.AccessibleRole.Graphic;
        _glyphLabel.AccessibleName = severity.ToString();
        Controls.Add(_glyphLabel);

        // Accent stays reserved for actions; the title reads as text, not a control.
        // 12 pt Segoe UI Bold needs ~22 px of line box; the shared 22 px row left the
        // descenders on the clip edge, so the title gets its own (still clearing y=38).
        var titleLabel = Label(title, TextXDip, 14, BodyWidthDip, ThemePalette.TextTitlePt, ThemePalette.TextPrimary, bold: true, height: 24);
        titleLabel.AccessibleName = title;
        titleLabel.AccessibleRole = WF.AccessibleRole.StaticText;
        Controls.Add(titleLabel);

        // The friendly-error lines are 45-77 characters, which is two to three of these
        // 244 DIP rows. A single ellipsed line cut off exactly the half that says what to do
        // about it ("Choose a different folder in Settings"), so the body wraps and the
        // window grows to whatever it needs, up to BodyMaxLines — which is also what Trim cuts to.
        _bodyColumnPx = S(BodyWidthDip);
        _previewLabel = Label(string.Empty, TextXDip, BodyTopDip, BodyWidthDip, ThemePalette.TextBodyLgPt, ThemePalette.TextSecondary);
        _previewLabel.AutoEllipsis = true;
        _previewLabel.UseMnemonic = false;
        _previewLabel.AccessibleRole = WF.AccessibleRole.StaticText;
        Controls.Add(_previewLabel);

        if (onOpen is not null)
        {
            _openButton = new DarkButton
            {
                Scale = _scale,
                BackColor = Back,
                // AccentDeep carries the white label at 4.6:1; the raw accent is 3.65:1.
                FillColor = ThemePalette.AccentDeep,
                Size = new SD.Size(S(96), S(26)),
                Text = "Open link",
            };
            _openButton.Click += (_, _) =>
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
            Controls.Add(_openButton);
        }

        if (extras?.SecondaryText is string secondaryText)
        {
            _secondaryButton = ActionButton(secondaryText, ThemePalette.SurfaceAlt);
            _secondaryButton.Click += (_, _) =>
            {
                try { extras.Secondary?.Invoke(); }
                catch (Exception ex) { Log.Error("Toast secondary action failed", ex); }
                Close();
            };
            Controls.Add(_secondaryButton);
        }

        if (extras?.ToggleText is string toggleText && extras.Toggle is Func<Task<string>> toggle)
        {
            _toggleButton = ActionButton(toggleText, ThemePalette.SurfaceAlt);
            _toggleButton.Click += async (_, _) =>
            {
                // Stays open on purpose: the point of the toggle is to look at the result
                // and change your mind again. Awaited, not discarded: the label must not
                // claim a re-copy that a busy clipboard refused.
                string label;
                try { label = await toggle(); }
                catch (Exception ex)
                {
                    Log.Error("Toast toggle failed", ex);
                    ShowActionFailure(FriendlyError.For(ex));
                    return;
                }
                if (IsDisposed) return;
                _toggleButton.Text = label;
                HoldOpen();
                Announce();
            };
            Controls.Add(_toggleButton);
        }

        if (extras?.CheckboxText is string checkboxText)
        {
            _suppressCheck = new DarkCheckBox
            {
                Scale = _scale,
                BackColor = Back,
                Font = ThemePalette.UiFont(ThemePalette.TextBodyPt),
                ForeColor = ThemePalette.TextSecondary,
                Text = checkboxText,
                AccessibleName = checkboxText,
            };
            _suppressCheck.CheckedChanged += (_, _) =>
            {
                try { extras.CheckboxChanged?.Invoke(_suppressCheck.Checked); }
                catch (Exception ex) { Log.Error("Toast preference change failed", ex); }
            };
            Controls.Add(_suppressCheck);
        }

        // FormBorderStyle.None with no Text leaves an unnamed window: Narrator announced the
        // tray balloon this replaced, so the toast has to name itself the same way.
        AccessibleRole = WF.AccessibleRole.Alert;
        AccessibleName = title;
        _body = string.Empty;
        SetBody(preview);

        // holdLong is for a working state ("Checking for updates…") whose work outlives the
        // confirmation dwell: it must not self-dismiss before its own result arrives.
        // Any toast carrying a control is long-dwell by construction: 1.7 s to read a
        // preview, decide, and hit a button is not an offer. This used to name _openButton
        // alone, because it was the only action a toast could carry when it was written.
        bool hasAction = _openButton is not null || _toggleButton is not null || _secondaryButton is not null;
        _dwellMs = hasAction || holdLong || severity == ToastSeverity.Error ? LongDwellMs : DwellMs;
        _dismiss.Interval = _sticky ? StickyBackstopMs : _dwellMs;
        _dismiss.Tick += (_, _) => Close();
        MouseEnter += (_, _) => HoldOpen();
        MouseLeave += (_, _) => RestartDismiss();

        // The toast is ShowWithoutActivation and never takes focus, so the Escape key never
        // reaches it. A click on the card is the only dismissal a user can actually perform,
        // and without one a 4.5 s error sits over whatever they were reaching for.
        Click += (_, _) => Close();
        foreach (WF.Control c in Controls)
        {
            c.MouseEnter += (_, _) => HoldOpen();
            c.MouseLeave += (_, _) => RestartDismiss();
            if (c is WF.Label) c.Click += (_, _) => Close();
        }
    }

    /// <summary>
    /// Puts a message in the body and grows the card to hold it. Both the constructor and
    /// the action-failure path go through here: the failure line is a friendly error two or
    /// three rows long arriving in a box that was sized for a one-line URL preview, and
    /// assigning only the text left the rest of it under the label's bottom edge.
    /// </summary>
    private void SetBody(string message)
    {
        _body = Trim(message, _bodyColumnPx);
        int lines = LinesNeeded(_body, _bodyColumnPx);

        _previewLabel.Text = _body;
        _previewLabel.AccessibleName = _body;
        _previewLabel.Height = S(BodyLineH * lines);
        _previewLabel.TextAlign = lines > 1 ? SD.ContentAlignment.TopLeft : SD.ContentAlignment.MiddleLeft;
        AccessibleDescription = _body;

        int bodyBottom = BodyTopDip + (BodyLineH * lines);

        // The button row fills from the right: primary last, then whatever declines or
        // toggles beside it, so the affirmative action is always in the same place.
        int rowRight = CardWidthDip - 14;
        bool hasRow = false;
        foreach (DarkButton? button in new[] { _openButton, _secondaryButton, _toggleButton })
        {
            if (button is null) continue;
            hasRow = true;
            rowRight -= 96;
            // ponytail: two buttons is the whole vocabulary (primary + one of decline/toggle),
            // and two fit exactly. Clamped rather than wrapped so a third would overlap on the
            // card instead of being painted off its left edge where nothing can reach it.
            button.Location = new SD.Point(S(Math.Max(TextXDip, rowRight)), S(bodyBottom + 8));
            rowRight -= 8;
        }

        int contentBottom = hasRow ? bodyBottom + 8 + 26 : bodyBottom;
        if (_suppressCheck is not null)
        {
            _suppressCheck.Location = new SD.Point(S(TextXDip), S(contentBottom + 10));
            contentBottom += 10 + 20;
        }

        ClientSize = new SD.Size(S(CardWidthDip), S(contentBottom + 10));
    }

    /// <summary>
    /// How many body rows <paramref name="columnPx"/> device pixels of width need for
    /// <paramref name="text"/>, capped at <see cref="BodyMaxLines"/>. Pass a toast's
    /// <see cref="BodyColumnPx"/> — the width of the label the text is actually going into.
    /// </summary>
    internal static int LinesNeeded(string text, int columnPx) =>
        Math.Clamp(RowsFor(text, columnPx), 1, BodyMaxLines);

    /// <summary>
    /// The uncapped row count, measured exactly the way the label draws: the same point-size
    /// <see cref="ThemePalette.UiFont"/>, in the label's own pixel column.
    ///
    /// It used to mix three DPIs — a pixel-unit probe font, GDI+'s screen DpiX for the column,
    /// and whatever system DPI TextRenderer had cached for the HFONT — and those never agree.
    /// GDI+ answered 96 while the process ran at 144, so a 28 px line box was measured against
    /// a 244 px column: the "no OCR language pack" line counted four rows, clamped to three,
    /// and Trim amputated the half that names the Settings page. Whether TextRenderer had
    /// cached 96 or 144 depended on which class ran first, which is what made the suite flake.
    /// One font and one column, both the label's own, cannot disagree with themselves.
    /// </summary>
    private static int RowsFor(string text, int columnPx)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        try
        {
            using SD.Font ui = ThemePalette.UiFont(ThemePalette.TextBodyLgPt);
            const WF.TextFormatFlags flags = WF.TextFormatFlags.WordBreak | WF.TextFormatFlags.NoPrefix;
            var column = new SD.Size(Math.Max(1, columnPx), int.MaxValue);

            // MeasureText adds a constant padding to the height on top of n line boxes, so
            // dividing by the font's line height over-counts. Two references give both terms.
            int one = WF.TextRenderer.MeasureText("Mg", ui, column, flags).Height;
            int two = WF.TextRenderer.MeasureText("Mg\nMg", ui, column, flags).Height;
            int lineH = Math.Max(1, two - one);
            int padding = one - lineH;

            SD.Size measured = WF.TextRenderer.MeasureText(text, ui, column, flags);
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
        ToastWindow? previous = _live;
        _live = this;
        if (!ReferenceEquals(previous, this) && previous is { IsDisposed: false })
        {
            // Close, so the outgoing toast runs its exit tween. Its Dispose only clears _live
            // when it still owns it, so the field this OnShown just claimed is safe.
            try { previous.Close(); }
            catch (Exception ex) { Log.Error("Could not close the outgoing toast (non-fatal)", ex); }
        }
        UpdateRegion();
        Position();
        _restLocation = Location;
        _motion?.Dispose();
        _motion = Motion.Run(RiseMs, Motion.EaseOutCubic, ApplyEntrance);
        // A sticky toast runs the backstop rather than the dwell; RestartDismiss picks.
        RestartDismiss();
        // Not gated on _sticky: the line-break toggle rides an ordinary 4.5 s toast, and a
        // control the pointer is the only way to reach is not a control everyone can use.
        if (AsksSomething)
            InstallKeyHook();
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
    /// Rewrites the live toast's body when it is still saying the same thing, and returns
    /// false when there is nothing on screen to rewrite so the caller can show a fresh one.
    /// This is the contract a progress readout needs: <see cref="OnShown"/> disposes the
    /// outgoing toast outright — deliberately, so two different messages never cross-dissolve
    /// — which turns "50 %" arriving after "45 %" into a strobe rather than a count.
    /// Deliberately does NOT <see cref="Announce"/>: re-announcing every step is the same
    /// strobe in audio, and the title a screen reader already heard has not changed.
    /// </summary>
    public static bool TryUpdateBody(string title, string body)
    {
        ToastWindow? live = _live;
        if (live is null || live.IsDisposed || live._exiting || !live.IsHandleCreated)
            return false;
        if (!string.Equals(live.AccessibleName, title, StringComparison.Ordinal))
            return false;

        live.SetBody(body);
        // The card may have changed height and a corner toast is bottom-anchored.
        live.Position();
        live._restLocation = live.Location;
        // Each step is fresh evidence the download is alive, so the dwell restarts with it.
        live._dismiss.Stop();
        live._dismiss.Start();
        return true;
    }

    /// <summary>
    /// The action failed. The toast becomes its own error report: the glyph and the body say
    /// so in place, rather than the card disappearing with nothing to show for the click.
    /// </summary>
    private void ShowActionFailure(string message)
    {
        if (IsDisposed) return;
        _dismiss.Stop();
        SetBody(message);
        _previewLabel.ForeColor = ThemePalette.TextError;
        _glyphLabel.Text = GlyphOf(ToastSeverity.Error);
        _glyphLabel.ForeColor = ColorOf(ToastSeverity.Error);
        _glyphLabel.AccessibleName = nameof(ToastSeverity.Error);
        if (IsHandleCreated)
        {
            // The card just changed height; a corner toast is bottom-anchored, so leaving it
            // where it was would push its new rows off the edge of the working area.
            Position();
            _restLocation = Location;
        }
        _dwellMs = LongDwellMs;
        RestartDismiss();
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

    /// <summary>Resumes the countdown after a hover. A sticky toast resumes its backstop
    /// instead of the dwell: moving the pointer away is not an answer, but it must still not
    /// leave an unanswerable window on screen for the rest of the session.</summary>
    private void RestartDismiss()
    {
        _dismiss.Interval = _sticky ? StickyBackstopMs : _dwellMs;
        _dismiss.Start();
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
            // EaseInCubic, not EaseOutCubic: the amount runs backwards here, so easing out
            // over reversed time would drop the card below 12 % opacity in the first 120 ms
            // and then leave an invisible window sitting there for the rest of the tween.
            _motion = Motion.Run(RiseMs, Motion.EaseInCubic, p => ApplyEntrance(from * (1d - p)), CloseIfStillExiting);
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
            if (ReferenceEquals(_live, this)) _live = null;
            RemoveKeyHook();
            _dismiss.Stop();
            _dismiss.Dispose();
            _motion?.Dispose();
            _motion = null;
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- keyboard
    // A sticky toast presents a decision, and this window is ShowWithoutActivation: it never
    // takes focus, so Tab, Enter and Escape all go to whatever the user was already in. That
    // left a TopMost decision card with no keyboard path to either answer. The fix is the
    // pattern FastKeystrokeOverlayWindow already uses -- a WH_KEYBOARD_LL hook, armed only
    // while a sticky toast that actually asks something is on screen -- rather than stealing
    // focus from the user's work.

    /// <summary>
    /// Whether this toast is putting a question to the user. Only one that is gets the hook:
    /// a sticky toast can also be a working state ("Reading text") that carries no buttons
    /// and asks nothing, and arming a machine-wide keyboard hook on its behalf takes keys
    /// away from whatever the user is actually typing in for the length of a job they did
    /// not ask to be interrupted by.
    ///
    /// Every control counts, not just the two buttons: the line-break toggle and the
    /// "Don't detect links" checkbox are both things the user has to answer, and naming only
    /// the buttons left them mouse-only on a window that never takes focus.
    /// </summary>
    private bool AsksSomething =>
        _openButton is not null || _secondaryButton is not null ||
        _toggleButton is not null || _suppressCheck is not null;

    /// <summary>Enter takes the affirmative: the primary where there is one, and otherwise
    /// the toggle, which on a toggle-only toast is the only thing there is to take.</summary>
    private DarkButton? EnterTarget => _openButton ?? _toggleButton;

    /// <summary>Space ticks the checkbox — what Space does to a checkbox everywhere else —
    /// and falls back to the toggle so a toggle-only toast answers to either key.</summary>
    private WF.Control? SpaceTarget => (WF.Control?)_suppressCheck ?? _toggleButton;

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private static readonly IntPtr Swallow = new(1);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private void InstallKeyHook()
    {
        if (_keyHook != IntPtr.Zero) return;
        _keyHookProc = KeyboardHookCallback; // held: a collected delegate takes the hook down
        _keyHook = SetWindowsHookExW(WhKeyboardLl, _keyHookProc, GetModuleHandleW(null), 0);
        if (_keyHook == IntPtr.Zero)
            Log.Error($"Toast could not install its keyboard hook (error {Marshal.GetLastWin32Error()})");
    }

    private void RemoveKeyHook()
    {
        if (_keyHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_keyHook);
        _keyHook = IntPtr.Zero;
        _keyHookProc = null;
    }

    /// <summary>
    /// Escape declines, from anywhere: it is the one key that can never do something the user
    /// did not ask for, and without it a sticky toast has no keyboard exit at all. It is only
    /// *consumed* while the pointer is over the card, though. This window never has focus, so
    /// swallowing Escape everywhere ate the Escape the user aimed at their own app -- the
    /// dropdown they meant to close stayed open and they had to press it twice. Declining a
    /// passive card in passing is safe; taking the key is not.
    ///
    /// Enter takes the primary, but only while the pointer is over the card -- hover is what
    /// says the toast is what the user is looking at, and an unconditional Enter would open a
    /// link out from under someone who had gone back to typing somewhere else. It is not
    /// swallowed when there is no primary to take, which was a key eaten for nothing.
    ///
    /// Space, on the same hover gate, is the other two controls' key: the checkbox where
    /// there is one, the toggle button otherwise. Without it "Cut breaks" and "Don't detect
    /// links" were reachable by pointer only.
    /// </summary>
    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !IsDisposed && !_exiting)
        {
            long msg = wParam.ToInt64();
            bool down = msg is WmKeyDown or WmSysKeyDown;
            if (down || msg is WmKeyUp or WmSysKeyUp)
            {
                var key = (WF.Keys)Marshal.PtrToStructure<KbdllHookStruct>(lParam).vkCode;
                if (key == WF.Keys.Escape)
                {
                    if (down) BeginInvoke(new Action(DeclineFromKeyboard));
                }
                else if (key is WF.Keys.Enter or WF.Keys.Return && down
                    && EnterTarget is DarkButton enter && PointerIsOver())
                {
                    BeginInvoke(new Action(() => enter.PerformClick()));
                }
                else if (key == WF.Keys.Space && down
                    && SpaceTarget is WF.Control space && PointerIsOver())
                {
                    BeginInvoke(new Action(() => PressWithSpace(space)));
                }

                if (ConsumesKey(key))
                    return Swallow;
            }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    /// <summary>Which keys this toast takes off the machine. Only what the pointer says the
    /// user is looking at: the hook sees every process's keystrokes, and this window has no
    /// focus to justify taking any of them.</summary>
    internal bool ConsumesKey(WF.Keys key) => key switch
    {
        WF.Keys.Escape => PointerIsOver(),
        WF.Keys.Enter or WF.Keys.Return => EnterTarget is not null && PointerIsOver(),
        WF.Keys.Space => SpaceTarget is not null && PointerIsOver(),
        _ => false,
    };

    /// <summary>Space on a checkbox ticks it; on the toggle button it presses it. Both hold
    /// the card open the way a click on either does, so answering with the keyboard does not
    /// leave the dwell running out under the answer.</summary>
    private void PressWithSpace(WF.Control target)
    {
        if (IsDisposed) return;
        HoldOpen();
        if (target is DarkCheckBox check) check.Checked = !check.Checked;
        else if (target is DarkButton button) button.PerformClick();
    }

    private bool PointerIsOver()
    {
        try { return IsHandleCreated && Bounds.Contains(WF.Cursor.Position); }
        catch { return false; }
    }

    /// <summary>Escape is the secondary answer where there is one ("Don't open"), and a plain
    /// dismissal where there is not.</summary>
    private void DeclineFromKeyboard()
    {
        if (IsDisposed) return;
        if (_secondaryButton is not null) _secondaryButton.PerformClick();
        else Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdllHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

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
        SD.Rectangle area = _screen.WorkingArea;
        // The insets are logical DIP like every other measurement in this file: unscaled, the
        // toast sat 16 physical px off the edge at 150 % where the quick-actions card sits 24,
        // which reads as a near-miss rather than as alignment.
        int edge = S(EdgeInsetDip);
        int gap = S(8);
        int x, y;
        if (_anchor is SD.Point a)
        {
            // Centered above the selection; flip below if there isn't room.
            x = a.X - Width / 2;
            y = a.Y - Height - edge;
            x = Math.Clamp(x, area.Left + gap, Math.Max(area.Left + gap, area.Right - Width - gap));
            if (y < area.Top + gap) y = a.Y + edge;
            y = Math.Clamp(y, area.Top + gap, Math.Max(area.Top + gap, area.Bottom - Height - gap));
        }
        else
        {
            // Nothing better to anchor to: the bottom-right corner of the monitor the user is
            // working on (the pointer's), which is where the balloon used to appear on a
            // single-monitor PC and is closer to the eye than the primary's tray on a stack.
            SD.Point corner = CornerPlacement(
                area, Size, OccupiedCorner(area), edge, gap);
            x = corner.X;
            y = corner.Y;
        }
        Location = new SD.Point(x, y);
    }

    /// <summary>
    /// Everything already standing in the bottom-right corner of <paramref name="area"/>: the
    /// quick-actions stack, plus the first-run bubble, which parks itself above the tray icon
    /// in the same corner and stays up until it is dismissed. Both are TopMost and both are
    /// interactive, so a toast landing on either hides an answer or swallows a click.
    /// </summary>
    private static SD.Rectangle OccupiedCorner(SD.Rectangle area)
    {
        SD.Rectangle cards = Overlay.FastQuickActionsWindow.StackBounds(area);
        SD.Rectangle bubble = FirstRunWelcomeWindow.LiveBounds;
        if (!bubble.IsEmpty && !area.IntersectsWith(bubble)) bubble = SD.Rectangle.Empty;
        if (cards.IsEmpty) return bubble;
        if (bubble.IsEmpty) return cards;
        return SD.Rectangle.Union(cards, bubble);
    }

    /// <summary>
    /// Where a toast with nothing to anchor to sits: the working area's bottom-right corner,
    /// lifted clear of <paramref name="cards"/> when the quick-actions stack is already there.
    /// The card and the toast default to the same corner at the same inset, and the card is
    /// pinned and interactive — a TopMost toast over it hides the thumbnail and swallows the
    /// first click aimed at Copy or Edit, for as long as an error dwells. Pass
    /// <see cref="SD.Rectangle.Empty"/> for <paramref name="cards"/> when no card is up.
    /// </summary>
    internal static SD.Point CornerPlacement(
        SD.Rectangle area, SD.Size size, SD.Rectangle cards, int edge, int gap)
    {
        int x = area.Right - size.Width - edge;
        int y = area.Bottom - size.Height - edge;
        if (!cards.IsEmpty && cards.IntersectsWith(new SD.Rectangle(x, y, size.Width, size.Height)))
            y = Math.Max(area.Top + gap, cards.Top - size.Height - gap);
        return new SD.Point(x, y);
    }

    /// <summary>
    /// Flattens the message to one paragraph and cuts it to what the body box can actually
    /// show. The cap used to be a flat 90 characters, which sat *inside* the three-row box and
    /// chopped the "no OCR language pack" line at "Time &amp; Language…", throwing away the half
    /// that names the Settings page — the only half that message exists for. The box knows its
    /// own capacity, so ask it, and cut on a word so what survives is a whole phrase.
    /// </summary>
    internal static string Trim(string text, int columnPx)
    {
        text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0) return text;

        // The walk below re-measures the whole remainder on every pass, so its cost is
        // quadratic in the length: 2 000 characters already cost a second on the UI thread.
        // Three rows hold well under 200 characters at any DPI, so cutting to a ceiling
        // first is free for every real message and bounds the walk for anything else.
        bool capped = text.Length > MaxChars;
        if (capped) text = text[..MaxChars];
        if (!capped && RowsFor(text, columnPx) <= BodyMaxLines)
            return text;

        string cut = text;
        while (cut.Length > 1 && RowsFor(cut + "…", columnPx) > BodyMaxLines)
        {
            int space = cut.LastIndexOf(' ', cut.Length - 2);
            cut = (space > 0 ? cut[..space] : cut[..(cut.Length - 1)]).TrimEnd();
        }
        return cut + "…";
    }

    private void UpdateRegion() => PopupChrome.ApplyRegion(this, S(14));

    private DarkButton ActionButton(string text, SD.Color fill) => new()
    {
        Scale = _scale,
        BackColor = Back,
        FillColor = fill,
        Size = new SD.Size(S(96), S(26)),
        Text = text,
    };

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
