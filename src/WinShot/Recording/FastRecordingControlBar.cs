using System.Diagnostics;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

/// <summary>
/// The controls for a running take: an owner-drawn capsule of glyph cells split by full-height
/// hairlines, anchored just below the recorded rectangle. Capture-excluded and draggable.
/// </summary>
public sealed class FastRecordingControlBar : WF.Form
{
    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private static readonly SD.Color Hairline = ThemePalette.BorderStrong;
    private static readonly SD.Color HoverFill = ThemePalette.HoverFill;
    private static readonly SD.Color GlyphColor = ThemePalette.TextPrimary;
    private static readonly SD.Color RecordingRed = ThemePalette.Red;
    private static readonly SD.Color PausedAmber = ThemePalette.Warn;

    // Segoe Fluent / MDL2 codepoints, one family and one size across the pill.
    private const string GlyphPause = "";
    private const string GlyphResume = "";
    private const string GlyphRestart = "";
    private const string GlyphStop = "";
    private const string GlyphDiscard = "";

    /// <summary>Idle transparency of the pill. The entrance tween ends here.</summary>
    private const double RestingOpacity = 0.96;

    /// <summary>What the window is called when no cell is hovered or focused.</summary>
    private const string IdleAccessibleName = "Recording controls";

    private readonly Stopwatch _elapsed = new();
    // 50ms is fast enough for a smooth breathing pulse without holding the
    // high-resolution clock for the whole recording.
    private readonly WF.Timer _timer = new() { Interval = 50 };
    private readonly WF.ToolTip _toolTip = new() { InitialDelay = 350, ReshowDelay = 120, OwnerDraw = true };
    private readonly IReadOnlyList<(RecordingControlBarCell Cell, SD.Rectangle Bounds)> _cells;
    private readonly SD.Font _glyphFont;
    private readonly SD.Font _timerFont;
    private readonly SD.Font _tipFont;
    private readonly SD.Rectangle? _recordingRegion;
    private readonly bool _showTimer;
    private readonly string _stopHotkey;
    private readonly double _scale;
    private IDisposable? _entrance;
    private IDisposable? _exit;
    // Stop swaps five controls for a spinner in the same slot: Motion.SwapMs is the token for
    // exactly that, and this amount cross-fades the two paint branches instead of cutting.
    private IDisposable? _saveFade;
    // The spinner is a rotating arc, which is the case where the 50 ms breathing tick's
    // ~46/62 ms jitter shows; while it runs the pill takes the real animation cadence.
    private IDisposable? _spinnerClock;
    private double _saveSwap;
    // Hover and press ease the same way every DarkButton in the app does; without them this
    // was the one surface the user touches during a take whose plate snapped on and off.
    private readonly HoverWash<int> _hover;
    private readonly HoverWash<int> _press;
    private readonly StateAmount _dim;
    private string _elapsedText = "00:00";
    private int _hoverIndex = -1;
    private int _focusIndex = -1;
    private bool _actionTaken;
    private bool _saving;
    private bool _closing;
    private bool _paused;
    private bool _tornDown;
    private int _dotAlpha = 255;

    public FastRecordingControlBar(
        SD.Rectangle? recordingRegion = null,
        bool showTimer = true,
        string? stopHotkey = null)
    {
        _recordingRegion = recordingRegion;
        _showTimer = showTimer;
        _stopHotkey = string.IsNullOrWhiteSpace(stopHotkey) ? "" : stopHotkey.Trim();
        // Point-based fonts render at the monitor's DPI, so the fixed-pixel cell layout must
        // scale with the target monitor or the glyphs overflow their cells on 125%/150%
        // displays. (AutoScaleMode.Dpi cannot: it only reacts to DPI *changes*.)
        SD.Rectangle targetScreen = recordingRegion is SD.Rectangle r
            ? WF.Screen.FromRectangle(r).Bounds
            : WF.Screen.FromPoint(WF.Cursor.Position).Bounds;
        _scale = RecordingMonitorDpi.ScaleFor(targetScreen);
        // Create the window ON its target monitor: born at (0,0) on the primary and moved
        // later, PerMonitorV2 fires WM_DPICHANGED on the move and rescales the window on
        // top of the pre-applied _scale (double-scaled bar on mixed-DPI setups).
        Location = new SD.Point(targetScreen.X, targetScreen.Y);

        _cells = RecordingControlBarLayout.Build(showTimer, _scale);
        _hover = new HoverWash<int>(InvalidateSafely);
        _press = new HoverWash<int>(InvalidateSafely);
        _dim = new StateAmount(InvalidateSafely);
        // Pixel-unit faces: every coordinate here is already multiplied by _scale, and a
        // point-size font would apply the monitor's scale to itself a second time.
        _glyphFont = ThemePalette.IconFontPx(S(16));
        _timerFont = ThemePalette.MonoFontPx(S(14));
        _tipFont = ThemePalette.UiFontPx(S(11));

        AutoScaleMode = WF.AutoScaleMode.None;
        // The pill is one owner-drawn form with no child controls, so a cell cannot be its own
        // accessible object. Naming the window after whatever cell is hovered or focused is
        // what lets a screen reader say "Pause recording" instead of announcing a blank
        // window — the same trick the recording HUD uses on the selector.
        AccessibleRole = WF.AccessibleRole.ToolBar;
        AccessibleName = IdleAccessibleName;
        BackColor = Back;
        ClientSize = RecordingControlBarLayout.Size(showTimer, _scale);
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Opacity = RestingOpacity;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        // The stock tooltip is a light system balloon over an otherwise fully owner-drawn
        // dark pill, so it is drawn in the palette too (same idiom as the All-in-One bar).
        _toolTip.Popup += (_, e) =>
            e.ToolTipSize = TipBoxSize(_toolTip.GetToolTip(e.AssociatedControl));
        _toolTip.Draw += (_, e) =>
        {
            using var bg = new SD.SolidBrush(Back);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var edge = new SD.Pen(Hairline, 1);
            e.Graphics.DrawRectangle(edge,
                new SD.Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1));
            WF.TextRenderer.DrawText(e.Graphics, e.ToolTipText, _tipFont, e.Bounds, GlyphColor,
                WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter);
        };

        MouseMove += OnBarMouseMove;
        MouseLeave += (_, _) => SetHover(-1);
        MouseDown += OnBarMouseDown;
        MouseUp += OnBarMouseUp;
        MouseClick += OnBarMouseClick;
        KeyDown += OnBarKeyDown;
        _timer.Tick += (_, _) =>
        {
            if (_saving)
            {
                Invalidate(); // the spinner arc sweeps across the whole pill
                return;
            }

            // Eased breathing instead of a binary blink: alpha rides a 1.6s sine — unless the
            // user has turned visual effects off system-wide, when the dot holds steady.
            double wave = Motion.AnimationsEnabled()
                ? (1 + Math.Sin(2 * Math.PI * Environment.TickCount64 / 1600.0)) / 2
                : 1;
            int alpha = _paused ? 255 : 120 + (int)Math.Round(135 * wave);
            string elapsed = FormatElapsed(_elapsed.Elapsed);
            if (alpha == _dotAlpha && elapsed == _elapsedText)
                return;
            _dotAlpha = alpha;
            _elapsedText = elapsed;
            InvalidateCell(RecordingControlBarCell.Timer);
        };
    }

    public event Action? StopRequested;
    public event Action? CancelRequested;
    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? RestartRequested;

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateWindowRegion();
        PositionUnderRegion();
        ExcludeFromCapture();
        PlayEntrance();
        if (_showTimer)
        {
            _elapsed.Start();
            _timer.Start();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        SD.Graphics g = e.Graphics;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var fill = new SD.SolidBrush(Back))
            g.FillRectangle(fill, ClientRectangle);

        // Both branches paint during the swap: the cells dissolve into the pill's own fill as
        // the spinner row rises out of it, so Stop reads as one surface changing what it says
        // rather than the controls vanishing between two frames.
        if (_saveSwap < 1)
        {
            for (int i = 0; i < _cells.Count; i++)
                PaintCell(g, i);
        }
        if (_saving)
            PaintSaving(g);

        PopupChrome.DrawBorder(g, ClientSize, Height / 2);
        base.OnPaint(e);
    }

    /// <summary>The cells this bar actually built, at the scale of the monitor it was built
    /// for. A test must scan these rather than recompute them at 100%, or it probes the gaps
    /// between cells on a scaled display.</summary>
    internal IReadOnlyList<(RecordingControlBarCell Cell, SD.Rectangle Bounds)> Cells => _cells;

    /// <summary>
    /// Paints the pill into a bitmap through the real OnPaint, so a test can pixel-scan exactly
    /// what ships rather than what DrawToBitmap happens to have cached.
    /// </summary>
    internal SD.Bitmap RenderToBitmap()
    {
        var image = new SD.Bitmap(
            Math.Max(1, ClientSize.Width),
            Math.Max(1, ClientSize.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(image))
        using (var capsule = GdiPaths.RoundedRect(
            new SD.Rectangle(SD.Point.Empty, image.Size), image.Height / 2))
        using (var args = new WF.PaintEventArgs(g, new SD.Rectangle(SD.Point.Empty, image.Size)))
        {
            // The window Region rounds the real pill; clip the same shape so what a harness
            // renders is the shape that ships rather than a square-cornered slab.
            g.SetClip(capsule);
            OnPaint(args);
        }
        return image;
    }

    protected override void OnClosed(EventArgs e)
    {
        TearDown();
        base.OnClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        // Form.Dispose() does not raise Closed, so a bar disposed without being closed (the
        // app shutting down mid-take) would otherwise keep a timer ticking against a dead
        // form and leak three font handles and a ToolTip per take. One guarded teardown,
        // called from both, the way the keystroke overlay and the marker surface do it.
        if (disposing)
            TearDown();
        base.Dispose(disposing);
    }

    private void TearDown()
    {
        if (_tornDown)
            return;

        _tornDown = true;
        _entrance?.Dispose();
        _entrance = null;
        _exit?.Dispose();
        _exit = null;
        _saveFade?.Dispose();
        _saveFade = null;
        _spinnerClock?.Dispose();
        _spinnerClock = null;
        _timer.Stop();
        _timer.Dispose();
        _toolTip.Dispose();
        _glyphFont.Dispose();
        _timerFont.Dispose();
        _tipFont.Dispose();
        _hover.Dispose();
        _press.Dispose();
        _dim.Dispose();
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    /// <summary>
    /// Fades in over an 8 px rise, so the pill arrives under the region rather than simply
    /// appearing there. Honours the system animation setting through Motion.Run.
    /// </summary>
    private void PlayEntrance()
    {
        int restY = Top;
        int rise = S(8);
        Opacity = 0;
        Top = restY + rise;
        _entrance?.Dispose();
        _entrance = Motion.Run(
            Motion.EnterDurationMs,
            Motion.EaseOutCubic,
            amount =>
            {
                if (IsDisposed)
                    return;
                Opacity = RestingOpacity * amount;
                Top = restY + (int)Math.Round(rise * (1 - amount));
            },
            () => _entrance = null);
    }

    /// <summary>
    /// Stop was pressed and the file is being written. The pill stays put, drops its controls
    /// for a "Saving…" row, and keeps sweeping a small arc so the wait is visibly the app
    /// working rather than the screen going blank between the last frame and the card.
    /// </summary>
    public void BeginSaving()
    {
        if (_saving || _closing)
            return;

        _saving = true;
        _actionTaken = true;
        _elapsed.Stop();
        _toolTip.SetToolTip(this, null);
        Cursor = WF.Cursors.Default;
        _hover.To(null);
        _press.To(null);
        _focusIndex = -1;
        AnnounceLabel("Saving the recording");
        // Runs even when the elapsed clock was never shown — but not at all when the user has
        // turned animation off, because then there is nothing left for it to move.
        if (Motion.AnimationsEnabled())
        {
            // A rotating arc at the breathing tick's 50 ms steps ~16 degrees a frame, and
            // against Windows' 15.6 ms tick that lands as a visible judder. Take the real
            // frame interval and hold the high-resolution clock for as long as it sweeps.
            _timer.Interval = Motion.FrameIntervalMs;
            _spinnerClock ??= Motion.Acquire();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }

        _saveFade?.Dispose();
        _saveFade = Motion.Run(
            Motion.SwapMs,
            Motion.EaseOutCubic,
            amount =>
            {
                _saveSwap = amount;
                InvalidateSafely();
            },
            () => _saveFade = null);
    }

    /// <summary>True once <see cref="BeginSaving"/> has run; for tests.</summary>
    internal bool IsSaving => _saving;

    /// <summary>How far the controls-to-spinner cross-fade has run, 0..1; for tests.</summary>
    internal double SavingAmount => _saveSwap;

    /// <summary>The cell the press wash is on, or null; for tests.</summary>
    internal double PressAmount(int index) => _press[index];

    /// <summary>
    /// Leaves the way it arrived: the entrance played backwards, then the window closes
    /// itself. Safe to call twice, and safe to call on a bar that never got shown.
    /// </summary>
    public void FadeOutAndClose()
    {
        if (_closing)
            return;

        _closing = true;
        _entrance?.Dispose();
        _entrance = null;
        _timer.Stop();
        _spinnerClock?.Dispose();
        _spinnerClock = null;
        if (IsDisposed || !IsHandleCreated)
        {
            CloseSafely();
            return;
        }

        int restY = Top;
        int sink = S(8);
        double from = Opacity;
        _exit?.Dispose();
        _exit = Motion.Run(
            Motion.EnterDurationMs,
            Motion.EaseInCubic,
            amount =>
            {
                if (IsDisposed)
                    return;
                Opacity = from * (1 - amount);
                Top = restY + (int)Math.Round(sink * amount);
            },
            () =>
            {
                _exit = null;
                CloseSafely();
            });
    }

    private void CloseSafely()
    {
        try
        {
            if (!IsDisposed)
                Close();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to close the recording control bar", ex);
        }
    }

    private void PositionUnderRegion()
    {
        SD.Rectangle region = _recordingRegion ?? SD.Rectangle.Empty;
        if (region.Width <= 0 || region.Height <= 0)
        {
            SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
            Location = RecordingControlBarPlacement.BottomCenter(area, Size);
            return;
        }

        Location = RecordingControlBarPlacement.UnderRegion(
            region,
            WF.Screen.FromRectangle(region).WorkingArea,
            Size,
            S(12));
    }

    // ---------------------------------------------------------------- painting

    private void InvalidateSafely()
    {
        if (!IsDisposed && IsHandleCreated)
            Invalidate();
    }

    private void PaintCell(SD.Graphics g, int index)
    {
        (RecordingControlBarCell cell, SD.Rectangle bounds) = _cells[index];
        // Hover rises, press deepens it further — the same direction DarkControlPalette moves
        // a neutral fill, eased over QuickMs by the two washes rather than flipped.
        double hover = _hover[index];
        double press = _press[index];
        double leave = _saveSwap;
        int veil = (int)Math.Round(((HoverFill.A * hover) + (0x1A * press)) * (1 - leave));
        if (cell != RecordingControlBarCell.Timer && veil > 0)
        {
            SD.Rectangle plate = SD.Rectangle.Inflate(bounds, -S(3), -S(6));
            using var path = GdiPaths.RoundedRect(plate, S(7));
            using var brush = new SD.SolidBrush(
                SD.Color.FromArgb(Math.Clamp(veil, 0, 255), 0xFF, 0xFF, 0xFF));
            g.FillPath(brush, path);
        }

        // Keyboard focus ring, so Tab traversal is visible on a bar of unlabelled glyphs.
        if (index == _focusIndex && Actionable(index))
        {
            SD.Rectangle ring = SD.Rectangle.Inflate(bounds, -S(3), -S(6));
            using var path = GdiPaths.RoundedRect(ring, S(7));
            using var pen = new SD.Pen(ThemePalette.AccentRing, Math.Max(1f, (float)(2 * _scale)));
            g.DrawPath(pen, path);
        }

        // Full-height hairline on the leading edge of every cell but the first.
        if (index > 0)
        {
            using var pen = new SD.Pen(Fade(Hairline, leave), 1);
            g.DrawLine(pen, bounds.Left, bounds.Top + S(9), bounds.Left, bounds.Bottom - S(9));
        }

        if (cell == RecordingControlBarCell.Timer)
        {
            PaintTimer(g, bounds, leave);
            return;
        }

        // TextError, not Red: this is a glyph drawn through the text path on ToolbarBg, the
        // exact pair ThemePalette documents as only reading 4.43:1. The dot stays Red.
        SD.Color color = cell == RecordingControlBarCell.Stop ? ThemePalette.TextError : GlyphColor;
        // Once an action is taken the cells go quiet. TextRenderer drops the alpha channel, so
        // the dim has to be composited against the plate rather than handed over as an alpha —
        // which also lets it ease instead of cutting.
        color = DarkControlPalette.Mix(
            color, ThemePalette.Over(SD.Color.FromArgb(0x60, color), Back), _dim.Value);
        WF.TextRenderer.DrawText(g, GlyphFor(cell), _glyphFont, bounds, Fade(color, leave),
            WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
            WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// Dissolves a colour into the pill's own fill by <paramref name="amount"/>. TextRenderer
    /// drops the alpha channel, so a text fade has to be composited against the backdrop
    /// rather than handed over as an alpha — the same reason the dim above composites.
    /// </summary>
    private static SD.Color Fade(SD.Color color, double amount) =>
        amount <= 0 ? color : DarkControlPalette.Mix(color, Back, amount);

    /// <summary>The whole pill becomes one row: a sweeping arc and the word for what is
    /// happening. No controls, because none of them mean anything now.</summary>
    private void PaintSaving(SD.Graphics g)
    {
        // The mirror of the cells' fade: the row arrives out of the fill they left through.
        double arrive = 1 - _saveSwap;
        int ring = S(14);
        var arc = new SD.Rectangle(
            S(RecordingControlBarLayout.SidePadDip) + S(4),
            (ClientSize.Height - ring) / 2,
            ring,
            ring);
        // 1.1 s per revolution — fast enough to read as busy, slow enough not to strobe. With
        // visual effects off the arc is parked and the word alone says what is happening.
        float start = Motion.AnimationsEnabled()
            ? (float)(Environment.TickCount64 % 1100 * 360.0 / 1100.0)
            : 270f;
        using (var track = new SD.Pen(Fade(Hairline, arrive), S(2)))
            g.DrawEllipse(track, arc);
        using (var sweep = new SD.Pen(Fade(GlyphColor, arrive), S(2)))
            g.DrawArc(sweep, arc, start, 100f);

        var textRect = new SD.Rectangle(
            arc.Right + S(8),
            0,
            ClientSize.Width - arc.Right - S(8) - S(RecordingControlBarLayout.SidePadDip),
            ClientSize.Height);
        using SD.Font font = ThemePalette.UiFontPx(S(13));
        WF.TextRenderer.DrawText(g, "Saving…", font, textRect, Fade(GlyphColor, arrive),
            WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter |
            WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
    }

    private void PaintTimer(SD.Graphics g, SD.Rectangle bounds, double leave)
    {
        int dot = S(9);
        var dotRect = new SD.Rectangle(
            bounds.Left + S(3),
            bounds.Top + ((bounds.Height - dot) / 2),
            dot,
            dot);
        using (var brush = new SD.SolidBrush(SD.Color.FromArgb(
            (int)Math.Round((_paused ? 255 : _dotAlpha) * (1 - leave)),
            _paused ? PausedAmber : RecordingRed)))
        {
            g.FillEllipse(brush, dotRect);
        }

        var textRect = new SD.Rectangle(
            dotRect.Right + S(5),
            bounds.Top,
            bounds.Right - dotRect.Right - S(5),
            bounds.Height);
        WF.TextRenderer.DrawText(g, _elapsedText, _timerFont, textRect, Fade(GlyphColor, leave),
            WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter |
            WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
    }

    private string GlyphFor(RecordingControlBarCell cell) => cell switch
    {
        RecordingControlBarCell.PauseResume => _paused ? GlyphResume : GlyphPause,
        RecordingControlBarCell.Restart => GlyphRestart,
        RecordingControlBarCell.Stop => GlyphStop,
        _ => GlyphDiscard,
    };

    /// <summary>
    /// The box the owner-drawn tooltip asks for. The padding scales with the face inside it:
    /// _tipFont is already a scaled pixel font, so a raw 12x8 gave a 22 px face at 200% a
    /// quarter of the breathing room an 11 px face gets at 100%.
    /// </summary>
    internal SD.Size TipBoxSize(string? text) => SD.Size.Add(
        WF.TextRenderer.MeasureText(text ?? "", _tipFont), new SD.Size(S(12), S(8)));

    /// <summary>Action plus its bound key, so a glyph-only cell still says what it does and
    /// how to reach it from the keyboard.</summary>
    internal string TipFor(RecordingControlBarCell cell) => cell switch
    {
        RecordingControlBarCell.PauseResume => _paused ? "Resume recording" : "Pause recording",
        RecordingControlBarCell.Restart => "Restart the take",
        RecordingControlBarCell.Stop => _stopHotkey.Length > 0
            ? $"Stop and save (Esc or {_stopHotkey})"
            : "Stop and save (Esc)",
        RecordingControlBarCell.Discard => "Discard the take (Shift+Esc)",
        _ => "",
    };

    private void InvalidateCell(RecordingControlBarCell cell)
    {
        foreach ((RecordingControlBarCell id, SD.Rectangle bounds) in _cells)
        {
            if (id == cell)
            {
                Invalidate(bounds);
                return;
            }
        }
    }

    // ---------------------------------------------------------------- input

    private void OnBarMouseMove(object? sender, WF.MouseEventArgs e) => SetHover(HitTest(e.Location));

    private void SetHover(int index)
    {
        if (_hoverIndex == index)
            return;

        _hoverIndex = index;
        bool actionable = Actionable(index);
        Cursor = actionable ? WF.Cursors.Hand : WF.Cursors.Default;
        _toolTip.SetToolTip(this, actionable ? TipFor(_cells[index].Cell) : null);
        _hover.To(actionable ? index : null);
        // The press also releases when the pointer leaves the cell it went down on, matching
        // the HUD's press-drag-off-release-nothing rule.
        if (!actionable || !Nullable.Equals(_press.Current, index))
            _press.To(null);
        Announce(actionable ? index : _focusIndex);
        Invalidate();
    }

    /// <summary>
    /// Renames the window after the cell under the pointer (or the focus ring) and tells the
    /// accessibility clients the name changed. The tooltip is a WinForms ToolTip and is not a
    /// UIA name, so without this the whole pill was one unnamed window to a screen reader.
    /// </summary>
    private void Announce(int index)
    {
        string label = Actionable(index) ? TipFor(_cells[index].Cell) : IdleAccessibleName;
        AnnounceLabel(label.Length == 0 ? IdleAccessibleName : label);
    }

    private void AnnounceLabel(string label)
    {
        if (string.Equals(AccessibleName, label, StringComparison.Ordinal))
            return;

        AccessibleName = label;
        if (!IsHandleCreated || IsDisposed)
            return;
        try { AccessibilityNotifyClients(WF.AccessibleEvents.NameChange, -1); }
        catch (Exception ex) { Log.Error("Could not announce the recording control cell", ex); }
    }

    /// <summary>
    /// Tab / Shift+Tab and Left / Right walk the actionable cells; Space or Enter works the
    /// focused one. Without it Pause and Restart were mouse-only — Escape reaches Stop and
    /// Discard, and nothing at all reached the other two.
    /// </summary>
    private void MoveFocus(int step)
    {
        int count = _cells.Count;
        for (int i = 1; i <= count; i++)
        {
            int next = _focusIndex < 0
                ? (step > 0 ? i - 1 : count - i)
                : ((((_focusIndex + (step * i)) % count) + count) % count);
            if (!Actionable(next))
                continue;
            _focusIndex = next;
            Announce(next);
            Invalidate();
            return;
        }
    }

    /// <summary>A cell the pointer can work: not the read-only timer, and not after the take
    /// has been stopped or is saving.</summary>
    private bool Actionable(int index) =>
        index >= 0 && !_actionTaken && !_saving &&
        _cells[index].Cell != RecordingControlBarCell.Timer;

    private int HitTest(SD.Point p)
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            if (_cells[i].Bounds.Contains(p))
                return i;
        }
        return -1;
    }

    private void OnBarMouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left)
            return;

        int hit = HitTest(e.Location);
        // Anywhere that is not a cell drags the pill; the cells keep their clicks.
        if (hit < 0)
        {
            Native.ReleaseCaptureAndDrag(Handle);
            return;
        }

        if (!_actionTaken && !_saving && _cells[hit].Cell != RecordingControlBarCell.Timer)
            _press.To(hit);
    }

    /// <summary>
    /// Releases the press wash. Without it the wash never came back down: HoverWash.To
    /// early-returns on an unchanged target, and Pause/Resume takes no terminal action, so a
    /// clicked Pause cell stayed at full press depth for the rest of the take.
    /// </summary>
    private void OnBarMouseUp(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Left)
            _press.To(null);
    }

    private void OnBarMouseClick(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left || _actionTaken)
            return;

        int hit = HitTest(e.Location);
        if (hit < 0)
            return;

        Activate(_cells[hit].Cell);
    }

    private void Activate(RecordingControlBarCell cell)
    {
        switch (cell)
        {
            case RecordingControlBarCell.PauseResume:
                TogglePause();
                break;
            case RecordingControlBarCell.Restart:
                RaiseOnce(RestartRequested);
                break;
            case RecordingControlBarCell.Stop:
                RaiseOnce(StopRequested);
                break;
            case RecordingControlBarCell.Discard:
                RaiseOnce(CancelRequested);
                break;
        }
    }

    /// <summary>
    /// Escape stops and keeps the take; discarding needs Shift. The bar is
    /// ShowWithoutActivation and rarely holds focus, which was the only thing making the old
    /// bare-Escape-throws-the-take-away behaviour survivable. Tab / arrows walk the cells so
    /// Pause and Restart — which no hotkey reaches — are workable without the mouse.
    /// </summary>
    private void OnBarKeyDown(object? sender, WF.KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case WF.Keys.Escape:
                e.Handled = true;
                RaiseOnce(e.Shift ? CancelRequested : StopRequested);
                return;
            case WF.Keys.Tab:
                MoveFocus(e.Shift ? -1 : 1);
                e.Handled = e.SuppressKeyPress = true;
                return;
            case WF.Keys.Left:
            case WF.Keys.Right:
                MoveFocus(e.KeyCode == WF.Keys.Right ? 1 : -1);
                e.Handled = e.SuppressKeyPress = true;
                return;
            case WF.Keys.Space:
            case WF.Keys.Enter:
                if (!Actionable(_focusIndex))
                    return;
                e.Handled = e.SuppressKeyPress = true;
                Activate(_cells[_focusIndex].Cell);
                return;
        }
    }

    /// <summary>The cell the keyboard ring is on, or -1. For tests.</summary>
    internal int FocusIndex => _focusIndex;

    /// <summary>Drives Tab / arrow traversal from a test without a real keyboard.</summary>
    internal void MoveFocusForTest(int step) => MoveFocus(step);

    /// <summary>Works the focused cell from a test, exactly as Space does.</summary>
    internal void ActivateFocusedForTest()
    {
        if (Actionable(_focusIndex))
            Activate(_cells[_focusIndex].Cell);
    }

    private void TogglePause()
    {
        if (_actionTaken)
            return;

        _paused = !_paused;
        if (_paused)
        {
            _elapsed.Stop();
            PauseRequested?.Invoke();
        }
        else
        {
            _elapsed.Start();
            ResumeRequested?.Invoke();
        }
        // The cell's label just changed under the pointer, so re-arm the tip in place rather
        // than clearing it: SetHover early-returns while the hovered index is unchanged, so a
        // cleared tip stayed empty until the pointer left the cell and came back.
        _toolTip.SetToolTip(
            this,
            Actionable(_hoverIndex) ? TipFor(_cells[_hoverIndex].Cell) : null);
        Announce(Actionable(_hoverIndex) ? _hoverIndex : _focusIndex);
        Invalidate();
    }

    /// <summary>Realigns the bar with the recorder's ACTUAL pause state. TogglePause flips
    /// optimistically before asking the recorder; when the pause/resume fails the bar was
    /// left showing a paused recording that was still running (or vice versa).</summary>
    public void SyncPaused(bool paused)
    {
        if (_paused == paused)
            return;

        _paused = paused;
        if (paused)
            _elapsed.Stop();
        else
            _elapsed.Start();
        Invalidate();
    }

    private void RaiseOnce(Action? action)
    {
        if (_actionTaken)
            return;

        _actionTaken = true;
        _toolTip.SetToolTip(this, null);
        Cursor = WF.Cursors.Default;
        _hover.To(null);
        _press.To(null);
        _focusIndex = -1;
        Announce(-1);
        _dim.To(1);
        Invalidate();
        action?.Invoke();
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        PopupChrome.ApplyRegion(this, Height / 2); // full capsule
    }

    private void ExcludeFromCapture()
    {
        try
        {
            if (!Native.SetWindowDisplayAffinity(Handle, Native.WdaExcludeFromCapture))
                Log.Info("SetWindowDisplayAffinity failed; control bar may appear in the recording");
        }
        catch (Exception ex)
        {
            Log.Error("Could not exclude recording bar from capture", ex);
        }
    }

    private int S(int logical) => RecordingMonitorDpi.Px(logical, _scale);

    private static class Native
    {
        public const uint WdaExcludeFromCapture = 0x11;
        private const int WmNclbuttondown = 0x00A1;
        private static readonly IntPtr HtCaption = new(2);

        public static void ReleaseCaptureAndDrag(IntPtr handle)
        {
            ReleaseCapture();
            SendMessage(handle, WmNclbuttondown, HtCaption, IntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }
}
