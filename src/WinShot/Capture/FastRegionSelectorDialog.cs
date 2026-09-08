using System.Runtime.InteropServices;
using WinShot.Core;
using WinShot.Recording;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Native lightweight region/window selector with two modes:
///   • <see cref="SelectorMode.Area"/> — draw a rectangular marquee, then MOVE/RESIZE it
///     (8 handles) and confirm with Enter or double-click. No window highlighting.
///   • <see cref="SelectorMode.Window"/> — highlight the window under the cursor and click
///     to capture that whole window. No marquee.
///
/// The crosshair + magnifier loupe follow the cursor continuously (the surface repaints on
/// every mouse-move). DPI correctness: one 1:1 overlay surface per monitor (primary monitor
/// = this Form/coordinator, others = <see cref="SelectorPane"/> children), and all selection
/// math is anchored to GetCursorPos (true physical px). Screen-freeze uses a desktop snapshot;
/// when disabled, native translucent surfaces leave the live desktop visible.
/// </summary>
public sealed partial class FastRegionSelectorDialog : WF.Form
{
    public enum SelectorMode { Area, Window, Measure }

    private const int DragThresholdPx = 4;

    /// <summary>
    /// Smallest drag a release is allowed to capture outright. Mouse-up confirm removed the
    /// step that used to catch a slip: with only a one-pixel floor, a press that travelled
    /// the 4 px drag threshold and let go wrote, copied and carded a 4x4 image. Anything
    /// under this falls back to the adjustable selection, so a wobble costs a keystroke
    /// instead of a capture the user has to notice, dismiss and redo.
    /// </summary>
    private const int MinConfirmDragPx = 8;

    /// <summary>Where the left button went down, for telling a click from a drag at release.</summary>
    private SD.Point _pressScreen;

    /// <summary>True while the left button is held on the detected element's shutter pill.</summary>
    private bool _pressedShutter;

    /// <summary>Set by <c>Complete</c> and cleared by <c>ResetForUse</c>. A pooled selector is
    /// parked, not destroyed, and keeps keyboard focus, so keys keep arriving after the
    /// session is over.</summary>
    private bool _sessionEnded;

    /// <summary>This session spent one of the first-run hint's five showings and has not
    /// written the new count to disk yet.</summary>
    private bool _hintSessionUnsaved;

    /// <summary>True for the sessions the first-run hint is shown in.</summary>
    private bool _hintEnabled;

    /// <summary>
    /// The edge of the pending selection the pointer is on, and the 0..1 amount its accent
    /// thickening is eased to. Clicking an edge snaps it up to <see cref="MarqueeEdge.ReachPx"/>
    /// away and the selector has no undo, so the edge has to say it is a target BEFORE the
    /// press: it thickens under the pointer and the cursor becomes a sizing arrow.
    /// </summary>
    private MarqueeSide _hoverEdge = MarqueeSide.None;
    private double _edgeGlow;
    private IDisposable? _edgeTween;

    /// <summary>The edge the left button went down on; the snap fires at release over the
    /// same edge, exactly as Cancel / Done / Pad do.</summary>
    private MarqueeSide _pressedEdge = MarqueeSide.None;

    /// <summary>True when the pointer has moved far enough from the press to be a drag.</summary>
    private bool HasTravelled(SD.Point at) =>
        Math.Abs(at.X - _pressScreen.X) >= DragThresholdPx ||
        Math.Abs(at.Y - _pressScreen.Y) >= DragThresholdPx;

    /// <summary>Peak alpha of the accent wash over a hover-targeted window.</summary>
    private const int WindowWashAlpha = 40;
    private static readonly SD.Color Accent = ThemePalette.Accent;

    private SD.Rectangle _vs = CaptureService.VirtualScreen;
    private SD.Rectangle _monitorBounds;
    private SettingsService? _settings;
    private SelectorOptions _options;
    private SelectorMode _mode = SelectorMode.Area;
    private bool _paneHover;
    /// <summary>Mode-specific instruction pill (scrolling capture). Fades out once the
    /// user starts dragging: by then it has been read or it never will be.</summary>
    private double _instructionAlpha;
    private IDisposable? _instructionFade;
    /// <summary>Alt-hover resolves the deepest UIA ELEMENT (normal captures) instead of the
    /// scrollable pane (scrolling captures).</summary>
    private bool _elementGranularity;
    private SD.Rectangle? _hoverPane;
    private readonly RectangleTween _hoverTween = new();
    /// <summary>Per-edge slack below which a re-detection counts as the same rect.</summary>
    private const int WobbleTolerancePx = 8;
    /// <summary>Every rect under the cursor that could plausibly be "the thing", smallest
    /// first. The wheel walks it, so a detector that picked the right area at the wrong depth
    /// costs one notch instead of a hand-drawn rectangle.</summary>
    private List<SD.Rectangle> _hoverLadder = new();
    private int _ladderIndex = -1;
    /// <summary>Distinct edge coordinates from the snap list, sorted, for marquee snapping.</summary>
    private int[] _snapEdgesX = Array.Empty<int>();
    private int[] _snapEdgesY = Array.Empty<int>();
    /// <summary>How close a dragged edge must come to a real one before it clicks onto it.</summary>
    private const int MarqueeSnapPx = 6;
    /// <summary>A rect the user explicitly picked with the wheel; held while the cursor
    /// stays inside it so the per-move resolution doesn't snap back to the innermost.</summary>
    private SD.Rectangle? _wheelPinned;
    /// <summary>True when the selection came from clicking the highlighted pane rather than
    /// a hand-drawn marquee — only pane clicks opt into probe refinement downstream; a rect
    /// the user deliberately drew is captured exactly as drawn.</summary>
    public bool SelectedByPaneClick { get; private set; }
    private List<WindowInfo> _windows = new();
    private List<SnapRect> _snapRects = new();
    private readonly List<SelectorPane> _panes = new();
    private SD.Point _dragStartScreen;     // physical screen px (GetCursorPos)
    private SD.Point _currentScreen;       // physical screen px (GetCursorPos)
    private SD.Point _lastFollowScreen;    // last cursor pos the crosshair/loupe were painted at
    private bool _dragging;                // drawing a fresh marquee
    private bool _dragMoved;
    private SD.Rectangle? _pendingScreen;  // the adjustable selection (Area mode), screen px
    private int _hoverBarButton = -1;      // 0 = Cancel, 1 = Done while hovered, else -1
    private int _pressedBarButton = -1;    // same encoding while the button is held down
    private int _resizeHandle = -1;        // 0..7 while dragging a handle, else -1
    private bool _movingPending;           // dragging the pending rect body to move it
    private SD.Point _adjustAnchor;        // cursor at the start of a move/resize
    private SD.Rectangle _adjustStartRect; // pending rect at the start of a move/resize
    private WindowInfo? _hoverWindow;
    /// <summary>Name of the app behind <see cref="_hoverWindow"/>, resolved once per target.</summary>
    private string _hoverAppName = string.Empty;
    /// <summary>0..1 ramp on the window-target wash, so retargeting reads as the tint moving
    /// rather than two rectangles blinking.</summary>
    private double _hoverWash;
    private IDisposable? _hoverWashTween;
    private SD.Bitmap? _frozen;            // frozen virtual-desktop snapshot shown under the overlay
    // Per-monitor frozen-slice-with-dim baked once, so each follow-frame is a single opaque
    // blit instead of re-cropping the snapshot AND alpha-blending a full-screen dim every tick
    // (the latter is what made the crosshair lag on large external monitors). Keyed by monitor.
    private readonly Dictionary<SD.Rectangle, SD.Bitmap> _dimmedCache = new();
    private SD.Bitmap? _capturedRegion;    // region cropped from _frozen at confirm; caller takes ownership
    private Func<Task<List<WindowInfo>>> _windowsProvider;
    private bool _windowsLoadStarted;
    private TaskCompletionSource<WF.DialogResult>? _completion;
    // Polls the cursor while a selection is open so the crosshair/loupe follow continuously,
    // independent of whether idle hover WM_MOUSEMOVE reaches the overlay (it doesn't reliably
    // across multiple monitors / when the overlay isn't the foreground window). Runs ONLY while
    // shown — started in ShowAsync, stopped in Complete — so it adds no idle background work.
    private readonly WF.Timer _followTimer;
    private bool _lastCtrlDown;
    // Session-effective loupe visibility: seeded from the "Show magnifier" setting each
    // ShowAsync, flipped by tapping Alt. The latch swallows key-repeat while Alt is held.
    private bool _magnifierOn;
    private bool _altLatched;
    // Recording HUD: the two-card settings/action pair that replaces the centred modal. Only
    // the recording flow turns it on; every other caller keeps the Cancel/Done bar.
    private bool _recordingHud;
    private RecordingHudToggles _recordingToggles;
    private int _aspectIndex = RecordingAspectPresets.FreeformIndex;
    private bool _aspectMenuOpen;
    private RecordingHudCell? _hudHover;
    private RecordingHudCell? _hudPressed;
    private int _aspectHover = -1;
    private RectangleTween? _aspectTween;
    private IDisposable? _aspectMotion;
    /// <summary>Where the running aspect glide is headed. Cancelling a Motion.Run skips its
    /// final frame, so the target has to be landed by hand when the glide is cut short.</summary>
    private SD.Rectangle? _aspectTarget;
    // Motion for the HUD. The two cards fade+rise in as a block; hover, press, the five
    // toggles and the aspect popover each cross-fade rather than snapping.
    private double _hudAmount;
    private bool _hudShown;
    private IDisposable? _hudMotion;
    private double _menuAmount;
    private IDisposable? _menuMotion;
    private HoverWash<RecordingHudCell>? _hudHoverWash;
    private HoverWash<RecordingHudCell>? _hudPressWash;
    private HoverWash<int>? _menuHoverWash;
    private StateAmount[]? _toggleAmounts;
    private StateAmount? _aspectChipAmount;
    /// <summary>Where the HUD last painted, so the exit tween can repaint that area after the
    /// pending selection is already gone.</summary>
    private SD.Rectangle _hudLastRect;
    private SD.Rectangle? _hudLastRegion;
    /// <summary>Keyboard focus on the HUD: index into <see cref="HudFocusOrder"/>, or -1.</summary>
    private int _hudFocus = -1;
    private static readonly RecordingHudCell[] HudFocusOrder =
    [
        RecordingHudCell.Aspect,
        RecordingHudCell.Microphone,
        RecordingHudCell.SystemAudio,
        RecordingHudCell.Webcam,
        RecordingHudCell.Clicks,
        RecordingHudCell.Keystrokes,
        RecordingHudCell.RecordGif,
        RecordingHudCell.RecordVideo,
    ];

    /// <summary>Set when the HUD confirmed a recording; null when the selector was used for
    /// anything else, or cancelled.</summary>
    public RecordingHudChoice? RecordingChoice { get; private set; }

    public FastRegionSelectorDialog(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        _settings = settings;
        _windowsProvider = windowsProvider;
        _monitorBounds = PrimaryBounds();

        SelectorChrome.ConfigureSurface(this);
        // Born translucent so the handle is created WS_EX_LAYERED once and never restyled:
        // any later Form.Opacity flip across the 1.0 boundary recreates the handle
        // (~150-500 ms measured). All opacity changes go through SetLayeredAlpha instead.
        SelectorChrome.ConfigurePresentation(this, freezeScreen: false);
        DoubleBuffered = true;
        SetStyle(PaintStyles, true);
        Bounds = _monitorBounds;

        _followTimer = new WF.Timer { Interval = WinShot.Core.Motion.FrameIntervalMs };
        _followTimer.Tick += OnFollowTick;

        // Window-list load is kicked from ShowAsync, not the Shown event: Shown fires only
        // on a form's FIRST display, so a pooled re-show would never load the list.

        ResetForUse(windowsProvider, settings);
    }

    public SD.Rectangle? SelectedRegionPx { get; private set; }

    /// <summary>
    /// Shift was held when the selection was confirmed, which asks for the bare capture with
    /// no composited backdrop. Read at confirm time rather than after the fact, because by
    /// the time the caller looks the key is usually already up.
    /// </summary>
    public bool BareBackgroundRequested { get; private set; }

    // DoubleBuffered + SetStyle are protected on Control, so each surface enables its own
    // flicker-free painting from inside its own constructor.
    private const WF.ControlStyles PaintStyles =
        WF.ControlStyles.AllPaintingInWmPaint |
        WF.ControlStyles.OptimizedDoubleBuffer |
        WF.ControlStyles.ResizeRedraw |
        WF.ControlStyles.UserPaint;

    private static SD.Rectangle PrimaryBounds() =>
        (WF.Screen.PrimaryScreen ?? WF.Screen.AllScreens[0]).Bounds;

    // One warm instance kept alive between captures: re-showing existing window handles
    // is near-instant, while creating the full-screen layered surfaces from scratch costs
    // 200-900 ms under CPU load. Hidden windows do no idle work and hold no bitmaps.
    private static FastRegionSelectorDialog? _pooled;

    public static FastRegionSelectorDialog Rent(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        var pooled = _pooled;
        _pooled = null;
        if (pooled is not null && !pooled.IsDisposed)
        {
            pooled.ResetForUse(windowsProvider, settings);
            return pooled;
        }
        return new FastRegionSelectorDialog(windowsProvider, settings);
    }

    public static void Return(FastRegionSelectorDialog selector)
    {
        if (selector.IsDisposed)
            return;

        selector.DisposeFrozen();
        selector._capturedRegion?.Dispose();
        selector._capturedRegion = null;
        selector.Park();

        var displaced = _pooled;
        _pooled = selector;
        if (displaced is not null && displaced != selector && !displaced.IsDisposed)
        {
            displaced.DisposePanes();
            displaced.Dispose();
        }
    }

    /// <summary>Creates the window handles (coordinator + panes) without showing anything,
    /// so the first hotkey press after app start skips window creation entirely.</summary>
    /// <summary>
    /// Makes the selector invisible without surrendering its DWM composition, so the next
    /// open is an alpha flip rather than ~130 ms of ShowWindow per full-screen surface.
    /// </summary>
    internal void Park()
    {
        // The selector is pooled, so an in-flight tween would otherwise outlive the session
        // that started it and keep the process-wide timer resolution raised.
        _instructionFade?.Dispose();
        _instructionFade = null;
        _instructionAlpha = 0d;
        SelectorSurfaces.Park(this);
        foreach (var pane in _panes)
            SelectorSurfaces.Park(pane);

        // Shown-but-parked, never hidden: Hide() would throw the composition away again.
        // Disposed panes report Visible==false, so guard: Show() on one would throw
        // ObjectDisposedException out of Complete() and wedge the selector session.
        if (!IsDisposed && !Visible) Show();
        foreach (var pane in _panes)
            if (!pane.IsDisposed && !pane.Visible) pane.Show();
    }

    internal void Prewarm()
    {
        CreatePanes();
        _ = Handle;
        foreach (var pane in _panes)
            _ = pane.Handle;
        // Pay the ShowWindow cost once, at startup, so even the first hotkey is instant.
        Park();
    }

    /// <param name="hint">Instructional pill shown until the first mouse-down. Empty for the
    /// screenshot flows, which have their own onboarding; the recording flow passes one
    /// because its selector is the only thing on screen when a take is being framed.</param>
    /// <param name="recordingHud">Toggles the two-card recording HUD on in place of the
    /// Cancel/Done bar, seeded with these toggle states.</param>
    /// <param name="initialSelection">Opens with this rect already pending (screen px), so a
    /// remembered or pre-picked region still goes through the same adjust-and-confirm chrome
    /// instead of skipping it.</param>
    public async Task<WF.DialogResult> ShowAsync(SelectorMode mode = SelectorMode.Area,
        bool paneHover = false, bool elementHover = false, string? instruction = null,
        string? hint = null,
        RecordingHudToggles? recordingHud = null,
        SD.Rectangle? initialSelection = null)
    {
        _mode = mode;
        _recordingHud = recordingHud is not null;
        _recordingToggles = recordingHud ?? default;
        _aspectIndex = RecordingAspectPresets.FreeformIndex;
        _aspectMenuOpen = false;
        _hudHover = null;
        _hudPressed = null;
        _hudFocus = -1;
        ResetHudMotion();
        RecordingChoice = null;
        _paneHover = paneHover || elementHover;
        _elementGranularity = elementHover;
        SelectedByPaneClick = false;
        _completion = new TaskCompletionSource<WF.DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _vs = CaptureService.VirtualScreen;
        if (initialSelection is SD.Rectangle seeded)
        {
            SD.Rectangle clipped = SD.Rectangle.Intersect(seeded, _vs);
            _pendingScreen = clipped.Width >= 2 && clipped.Height >= 2 ? clipped : null;
        }
        _monitorBounds = PrimaryBounds();
        Bounds = _monitorBounds;
        _options = SelectorOptions.ForRegion(_settings?.Current) with { Instruction = instruction };
        if (mode == SelectorMode.Measure)
        {
            _options = _options.ForMeasure();
            ResetMeasure();
            _measure.SetTolerance(MeasureToleranceFromSettings());
        }
        _instructionFade?.Dispose();
        _instructionFade = null;
        // Faded in, not simply there: FadeInstructionOut already runs a proper ramp when the
        // drag begins, and an entrance that is not its mirror is the asymmetry the rubric asks
        // about. This is the pill alone -- the scrim stays un-ramped, for open latency.
        _instructionAlpha = 0d;
        // Always open translucent over the live desktop so the overlay appears the instant
        // the hotkey fires. The freeze snapshot (WGC setup alone can cost 300-1700 ms under
        // load) is taken in the background — these surfaces are excluded from capture — and
        // bakes in underneath when it lands; the dim level matches, so the swap is invisible.
        SelectorChrome.ConfigurePresentation(this, freezeScreen: false);
        DisposeFrozen();
        // Snapshot every snappable HWND rect BEFORE our own overlay windows go up, so the
        // list can never contain the selector itself (pooled surfaces are hidden here, and
        // invisible windows are filtered). Cheap (a few ms) and done once — hover detection
        // is then a pure in-memory scan with no per-move syscalls. Synchronous on purpose:
        // it must finish before Show(), and a Task.Run hop under CPU load would delay the
        // open longer than the scan itself.
        // Measure mode wants them too: a placed guide runs through the same SnapCoordinate
        // the marquee uses, so it clicks onto a real element edge for nothing.
        _snapRects = _paneHover || mode == SelectorMode.Measure
            ? WindowEnumerator.GetSnapRectangles()
            : new List<SnapRect>();
        _snapEdgesX = _snapRects.SelectMany(r => new[] { r.Bounds.Left, r.Bounds.Right })
            .Distinct().OrderBy(x => x).ToArray();
        _snapEdgesY = _snapRects.SelectMany(r => new[] { r.Bounds.Top, r.Bounds.Bottom })
            .Distinct().OrderBy(y => y).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CreatePanes();
        // Warm (pooled) surfaces may still be opaque from a previous frozen swap.
        SelectorSurfaces.Unpark(this, SelectorChrome.LiveAlpha);
        foreach (var pane in _panes)
            SelectorSurfaces.Unpark(pane, SelectorChrome.LiveAlpha);
        long panesMs = sw.ElapsedMilliseconds;

        Show();
        FadeInstructionIn();
        StartWindowLoad();
        long showMs = sw.ElapsedMilliseconds - panesMs;
        var paneTimings = new List<string>(_panes.Count);
        foreach (var pane in _panes)
        {
            long before = sw.ElapsedMilliseconds;
            bool warm = pane.IsHandleCreated;
            pane.Show();
            paneTimings.Add($"{pane.MonitorBounds.Width}x{pane.MonitorBounds.Height}" +
                $"{(warm ? "" : "(cold)")}={sw.ElapsedMilliseconds - before}");
        }
        long paneShowMs = sw.ElapsedMilliseconds - panesMs - showMs;

        Activate();
        Focus();
        SelectorForeground.Restore(this);
        if (sw.ElapsedMilliseconds > 100)
        {
            Log.Info(
                "Perf selector open breakdown: " +
                $"panes={panesMs} show={showMs} paneShow={paneShowMs} " +
                $"[{string.Join(" ", paneTimings)}] " +
                $"activate={sw.ElapsedMilliseconds - panesMs - showMs - paneShowMs} total={sw.ElapsedMilliseconds} ms");
        }
        // Only now: Motion.Run is driven off a Stopwatch, so starting the HUD's 280 ms rise
        // before the panes exist spent it on window creation and a seeded region opened with
        // the cards already part-way (or fully) up.
        if (_recordingHud && _pendingScreen is not null)
            ShowHud(true);
        _lastCtrlDown = false;
        _magnifierOn = _options.ShowMagnifier;
        _altLatched = false;
        _currentScreen = CursorScreen();
        _lastFollowScreen = _currentScreen;
        _hintOpacity = 0d;
        if (!string.IsNullOrEmpty(hint))
        {
            // A caller-supplied line (the recording flow, whose selector is the only thing on
            // screen while a take is framed) is unconditional rather than session-gated, and
            // rides the same one pill as the first-run hint. A seeded selection has nothing
            // left to instruct, so it opens silent.
            _hintEnabled = true;
            _hintText = hint;
            _hintIsMessage = false;
            if (_pendingScreen is null)
            {
                FadeHint(arriving: true);
                StartFollowMotion();
            }
        }
        else if (_settings is not null && SelectorHint.ShouldShow(_settings.Current.SelectorHintSessions))
        {
            _settings.Current.SelectorHintSessions =
                SelectorHint.NextSessionCount(_settings.Current.SelectorHintSessions);
            _hintSessionUnsaved = true;
            _hintEnabled = true;
            _hintText = SelectorHint.Text(_mode == SelectorMode.Window);
            _hintIsMessage = false;
            FadeHint(arriving: true);
            StartFollowMotion(); // the pill is drawn on the follow frames
        }
        if (_options.NeedsCursorFollow || _paneHover || mode == SelectorMode.Measure)
            StartFollowMotion();
        if (mode == SelectorMode.Measure)
            StartHintEntrance();
        if (_options.FreezeScreen)
            _ = FreezeWhileShownAsync(_completion);
        return await _completion.Task;
    }

    /// <summary>
    /// Grabs the freeze snapshot while the selector is already on screen and switches the
    /// surfaces from translucent-live to opaque-frozen once it lands. Everything after the
    /// await runs on the UI thread, serialized with Complete(), so the session guard is
    /// race-free. If the session ended first the late snapshot is discarded; if the capture
    /// failed we simply stay on the live translucent view.
    /// </summary>
    private async Task FreezeWhileShownAsync(TaskCompletionSource<WF.DialogResult> session)
    {
        // Capture into a LOCAL. The selector is pooled, so a cancelled session's capture
        // can still be in flight when the next session opens; a stale continuation must
        // never assign to (or dispose) the shared _frozen/_dimmedCache the live session
        // paints from — the old code did both, and when the stale (slow) capture landed
        // after the new (fast) one, it nulled the live snapshot while every surface was
        // already opaque: a solid-black overlay across all monitors.
        SD.Bitmap? snapshot = null;
        try
        {
            snapshot = await Task.Run(CaptureService.CaptureVirtualDesktop);
        }
        catch (Exception ex)
        {
            Log.Error("Screen-freeze capture failed; selecting over a plain dim instead", ex);
        }

        if (_completion != session || IsDisposed)
        {
            snapshot?.Dispose(); // stale: touch nothing shared
            return;
        }

        DisposeFrozen();
        _frozen = snapshot;
        if (_frozen is null)
        {
            // Measure mode has nothing to read and no way to get it; say so rather than
            // leaving a reticle and a hint strip that will never measure anything. The toast
            // dwells out in a few seconds, so the failure is latched too: from here the hint
            // strip and every gesture's answer say the mode cannot work and how to leave it.
            NoteFrozenSnapshotFailed();
            if (_mode == SelectorMode.Measure)
            {
                // Through MeasureToast, so this lands on the monitor being measured like
                // every other message the mode raises — a failure reported on the primary's
                // tray corner is a failure reported on a screen the user is not looking at.
                MeasureToast("The screen could not be read, so there is nothing to measure.",
                    ToastSeverity.Error);
            }
            return;
        }

        // Live mode paints solid black behind a 45% layered alpha; frozen mode paints the
        // dimmed snapshot at full alpha. The two look identical on screen (0.553*desktop vs
        // 0.549*desktop) — but only once the frozen paint has actually landed. Flipping alpha
        // first left every surface fully opaque over its live-mode BLACK for as long as the
        // first frozen paint took, which is a full-monitor bitmap allocation plus two blits
        // per surface: a black flash across every monitor, worst on the first hotkey.
        //
        // So the dimmed slices are built first. There is deliberately no await between here
        // and the repaint, so WinForms cannot pump a paint in the half-swapped state.
        var swap = System.Diagnostics.Stopwatch.StartNew();
        GetDimmedBackground(_monitorBounds);
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed)
                GetDimmedBackground(pane.MonitorBounds);
        }
        long prewarmMs = swap.ElapsedMilliseconds;

        // The snapshot IS the measurement's input, and it lands 300-1700 ms after the mode
        // opens. Nothing else re-reads it: the follow tick returns early while the pointer
        // has not moved, so without this the mode shows a reticle and no measurement until
        // the user jiggles the mouse.
        NoteFrozenSnapshotLanded();

        // Per surface: paint the frozen frame FIRST, then go opaque. There is no ordering
        // that is completely invisible — while the live desktop still shows through, the
        // composite cannot equal the frozen one — but the two artifacts are not equal:
        //   flip first  -> the surface is opaque over live mode's BLACK until the paint
        //                  lands: a full-screen black flash, and it reads as a fault.
        //   paint first -> for the length of that one paint the surface shows the frozen
        //                  image THROUGH the 45% alpha, i.e. briefly brighter, which reads
        //                  as the dim settling in.
        // Doing it per surface also means no monitor waits on another monitor's paint.
        long alphaStart = swap.ElapsedMilliseconds;
        Invalidate();
        Update();
        WinShot.Scrolling.CaptureExclusion.SetLayeredAlpha(Handle, 255);
        foreach (var pane in _panes)
        {
            if (pane.IsDisposed || !pane.IsHandleCreated) continue;
            pane.Invalidate();
            pane.Update();
            WinShot.Scrolling.CaptureExclusion.SetLayeredAlpha(pane.Handle, 255);
        }
        Log.Info($"Perf freeze swap: prewarm={prewarmMs} alpha={alphaStart - prewarmMs} " +
            $"paint={swap.ElapsedMilliseconds - alphaStart} ms");
    }

    private void ResetForUse(Func<Task<List<WindowInfo>>> windowsProvider, SettingsService? settings)
    {
        _vs = CaptureService.VirtualScreen;
        _monitorBounds = PrimaryBounds();
        _settings = settings;
        _options = SelectorOptions.ForRegion(settings?.Current);
        _mode = SelectorMode.Area;
        _windowsProvider = windowsProvider;
        _windowsLoadStarted = false;
        _windows = new List<WindowInfo>();
        _snapRects = new List<SnapRect>();
        _snapEdgesX = Array.Empty<int>();
        _snapEdgesY = Array.Empty<int>();
        _dragging = false;
        _dragMoved = false;
        // Before nulling the pending rect, not after: StopAspectSnap lands the glide by writing
        // its target back into _pendingScreen, so the other order handed a pooled selector a
        // phantom selection the user never drew.
        StopAspectSnap();
        _pendingScreen = null;
        _recordingHud = false;
        _recordingToggles = default;
        _aspectIndex = RecordingAspectPresets.FreeformIndex;
        _aspectMenuOpen = false;
        _hudHover = null;
        _hudPressed = null;
        _hudFocus = -1;
        ResetHudMotion();
        RecordingChoice = null;
        _hoverBarButton = -1;
        _pressedBarButton = -1;
        _pressedShutter = false;
        _resizeHandle = -1;
        _movingPending = false;
        _pressedEdge = MarqueeSide.None;
        _hoverEdge = MarqueeSide.None;
        _edgeTween?.Dispose();
        _edgeTween = null;
        _edgeGlow = 0d;
        _sessionEnded = false;
        _hintEnabled = false;
        _handleTween?.Dispose();
        _handleTween = null;
        _handleScale = 1d;
        _hintTween?.Dispose();
        _hintTween = null;
        _hintHold?.Dispose();
        _hintHold = null;
        _hintOpacity = 0d;
        _hintText = string.Empty;
        _hintIsMessage = false;
        _barTween?.Dispose();
        _barTween = null;
        Array.Clear(_barHover);
        Array.Clear(_barPress);
        _pillTween?.Dispose();
        _pillTween = null;
        _pillHover = false;
        _pillSwap = 0d;
        _pendingGlideTween?.Dispose();
        _pendingGlideTween = null;
        _pendingGlide.Stop();
        _hoverWindow = null;
        _hoverAppName = string.Empty;
        // A pooled selector outlives the windows it saw last session; a stale handle can be
        // reused by a different process, and a fixed-size cache with no clear point grows.
        _appNames.Clear();
        BeginHoverWash(arriving: false, wasHovering: false);
        _paneHover = false;
        _elementGranularity = false;
        _hoverPane = null;
        _hoverTween.Stop();
        _hoverLadder = new List<SD.Rectangle>();
        _ladderIndex = -1;
        _wheelPinned = null;
        SelectedByPaneClick = false;
        SelectedRegionPx = null;
        BareBackgroundRequested = false;
        ResetMeasure();
        DisposeFrozen();
        _capturedRegion?.Dispose();
        _capturedRegion = null;
        DialogResult = WF.DialogResult.None;
        Bounds = _monitorBounds;
        Capture = false;
        // Seed at the real cursor so the first paint draws the crosshair/loupe at the
        // pointer instead of a corner until the first mouse-move arrives.
        _currentScreen = CursorScreen();
        _completion = null;
    }

    // ----------------------------------------------------------- per-monitor panes

    private void CreatePanes()
    {
        var targets = new List<SD.Rectangle>();
        foreach (var screen in WF.Screen.AllScreens)
        {
            if (screen.Bounds != _monitorBounds)
                targets.Add(screen.Bounds);
        }

        // Reuse the existing (hidden) panes when the monitor layout is unchanged — their
        // window handles are warm, so re-showing them is near-instant.
        if (_panes.Count == targets.Count &&
            _panes.All(p => !p.IsDisposed) &&
            targets.All(b => _panes.Any(p => p.MonitorBounds == b)))
        {
            return;
        }

        DisposePanes();
        foreach (var bounds in targets)
        {
            // Panes open translucent like the coordinator; FreezeWhileShownAsync flips
            // them opaque when the snapshot lands.
            var pane = new SelectorPane(this, bounds, freezeScreen: false);
            _panes.Add(pane);
        }
    }

    private void DisposePanes()
    {
        foreach (var pane in _panes)
        {
            try { pane.Close(); pane.Dispose(); }
            catch { /* best effort */ }
        }
        _panes.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Close, do not dispose: see the _measureMenu note. By the time the form itself
            // is being disposed the modal-menu hook is long done with it, so the deferred
            // dispose can finally happen — otherwise one drop-down leaks per process and
            // per pool displacement.
            CloseMeasureMenu();
            DisposePreviousMeasureMenu();
            // Disposing mid-animation must give the high-resolution clock back.
            StopHintEntrance();
            StopPillHover();
            DisposePanes();
            DisposeFrozen();
            _hoverWashTween?.Dispose();
            _hoverWashTween = null;
            _handleTween?.Dispose();
            _handleTween = null;
            _hintTween?.Dispose();
            _hintTween = null;
            _hintHold?.Dispose();
            _hintHold = null;
            _barTween?.Dispose();
            _barTween = null;
            _pillTween?.Dispose();
            _pillTween = null;
            _pendingGlideTween?.Dispose();
            _pendingGlideTween = null;
            _edgeTween?.Dispose();
            _edgeTween = null;
            DisposeHudMotion();
            _followTimer.Dispose();
            _followMotionClock?.Dispose();
            _followMotionClock = null;
            _capturedRegion?.Dispose();
            _capturedRegion = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>Repaints the coordinator surface and every pane (the selection can span monitors).</summary>
    private void InvalidateAllSurfaces()
    {
        InvalidationLog?.Add(_monitorBounds);
        Invalidate();
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed)
                pane.Invalidate();
        }
        _lastFollowScreen = _currentScreen; // a full repaint redraws the crosshair at the cursor
    }

    // Half-extents of the regions the idle crosshair/loupe occupy around the cursor.
    private const int CrosshairBandHalf = 16;  // guide-line band half-width (covers line + shadow + AA)
    private const int LoupeBoxHalf = 250;       // box that contains the loupe + label in any flip

    /// <summary>
    /// Invalidates just the crosshair guide bands (full monitor height/width strips at the old
    /// and new cursor X/Y) and the loupe boxes, on whichever surfaces they touch. This makes
    /// the follow repaint proportional to the thin bands, not the whole (possibly 4K) monitor.
    /// </summary>
    private void InvalidateFollowRegion(SD.Point oldScreen, SD.Point newScreen)
    {
        InvalidateFollowAt(oldScreen);
        if (newScreen != oldScreen)
            InvalidateFollowAt(newScreen);
    }

    private void InvalidateFollowAt(SD.Point pt)
    {
        SD.Rectangle mon = MonitorBoundsAt(pt);
        InvalidateScreenRect(new SD.Rectangle(pt.X - CrosshairBandHalf, mon.Top, CrosshairBandHalf * 2, mon.Height));
        InvalidateScreenRect(new SD.Rectangle(mon.Left, pt.Y - CrosshairBandHalf, mon.Width, CrosshairBandHalf * 2));
        InvalidateScreenRect(new SD.Rectangle(pt.X - LoupeBoxHalf, pt.Y - LoupeBoxHalf, LoupeBoxHalf * 2, LoupeBoxHalf * 2));
        if (_instructionAlpha > 0d && InstructionRectAt(pt) is SD.Rectangle instruction)
            InvalidateScreenRect(instruction);
    }

    /// <summary>Render seam: the monitors a test wants <see cref="MonitorBoundsAt"/> to answer
    /// with. A pane is a real window and a test on the hidden desktop may not open one per
    /// monitor, so without this every multi-monitor arithmetic in the selector is untestable —
    /// and Measure mode has chrome that must reach a monitor the pointer has LEFT.</summary>
    internal IReadOnlyList<SD.Rectangle>? MonitorsForRender { get; set; }

    /// <summary>Render seam: every repaint asked for since the list was handed in, as screen
    /// rects (a full-surface repaint logs the coordinator's own bounds). A surface nobody asked
    /// to repaint is exactly what a ghost is made of, and invalidation is otherwise invisible
    /// to a test.</summary>
    internal List<SD.Rectangle>? InvalidationLog { get; set; }

    /// <summary>The physical bounds of the surface (coordinator or pane) containing a screen point.</summary>
    private SD.Rectangle MonitorBoundsAt(SD.Point screen)
    {
        if (MonitorsForRender is { } fake)
        {
            foreach (var bounds in fake)
                if (bounds.Contains(screen))
                    return bounds;
        }

        if (_monitorBounds.Contains(screen)) return _monitorBounds;
        foreach (var pane in _panes)
            if (!pane.IsDisposed && pane.MonitorBounds.Contains(screen))
                return pane.MonitorBounds;
        return WF.Screen.FromPoint(screen).Bounds;
    }

    /// <summary>The DPI factor of the surface a screen point lands on. Every piece of
    /// chrome that is measured in logical px goes through this, so the handles, the edge
    /// band and the pills all grow together on Isaiah's 150 % primary.</summary>
    private double ScaleAt(SD.Point screen) =>
        WinShot.Recording.RecordingMonitorDpi.ScaleFor(MonitorBoundsAt(screen));

    /// <summary>Invalidates a screen-space rectangle on every surface it intersects.</summary>
    private void InvalidateScreenRect(SD.Rectangle screenRect)
    {
        InvalidationLog?.Add(screenRect);
        InvalidateSurfaceRect(this, _monitorBounds, screenRect);
        foreach (var pane in _panes)
            if (!pane.IsDisposed)
                InvalidateSurfaceRect(pane, pane.MonitorBounds, screenRect);
    }

    private static void InvalidateSurfaceRect(WF.Control surface, SD.Rectangle surfaceScreen, SD.Rectangle screenRect)
    {
        var hit = SD.Rectangle.Intersect(surfaceScreen, screenRect);
        if (hit.Width <= 0 || hit.Height <= 0)
            return;
        surface.Invalidate(new SD.Rectangle(hit.X - surfaceScreen.X, hit.Y - surfaceScreen.Y, hit.Width, hit.Height));
    }

    // ----------------------------------------------------------- window list

    private void StartWindowLoad()
    {
        if (_windowsLoadStarted)
            return;

        _windowsLoadStarted = true;
        _ = LoadWindowsAsync(_windowsProvider());
    }

    private async Task LoadWindowsAsync(Task<List<WindowInfo>> windowsTask)
    {
        try
        {
            var windows = await windowsTask.ConfigureAwait(false);
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed)
                        _windows = windows;
                }));
            }
            catch (InvalidOperationException)
            {
                _windows = windows;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load selector window list", ex);
        }
    }

    // ----------------------------------------------------------- input (coordinator)

    protected override void OnKeyDown(WF.KeyEventArgs e)
    {
        HandleKeyDown(e);
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(WF.KeyEventArgs e)
    {
        HandleKeyUp(e);
        base.OnKeyUp(e);
    }

    protected override void OnFormClosing(WF.FormClosingEventArgs e)
    {
        if (_completion is not null)
        {
            e.Cancel = true;
            Complete(WF.DialogResult.Cancel);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnMouseDown(WF.MouseEventArgs e)
    {
        HandleMouseDown(e);
        Capture = CapturingPointer;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        HandleMouseMove();
        Cursor = CursorForCurrent();
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(WF.MouseEventArgs e)
    {
        Capture = false;
        HandleMouseUp(e);
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(WF.MouseEventArgs e)
    {
        HandleMouseWheel(e);
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        // No Clear here: PaintSurface's first act covers the whole surface, either with the
        // frozen slice or with a full-surface dim, and a redundant full-monitor fill is
        // expensive enough at 4K-ish sizes to show up in the freeze swap.
        if (!HasFullSurfaceBackground)
            e.Graphics.Clear(SD.Color.Black);
        PaintSurface(e.Graphics, _monitorBounds);
        base.OnPaint(e);
    }

    // The pointer is "captured" (events keep flowing to the pressed surface) while drawing a
    // marquee or dragging a handle/the pending rect.
    internal bool CapturingPointer => _dragging || _resizeHandle >= 0 || _movingPending;

    /// <summary>
    /// Whether a letter key arrived on its own. The selector's single-letter shortcuts match
    /// on KeyCode, so without this Ctrl+A — the near-universal "select all" reflex, on an
    /// overlay that has a selection on it — silently re-snapped every edge of the user's
    /// rectangle. Ctrl+C copying the colour is deliberate and is checked separately.
    /// </summary>
    private static bool IsBareKey(WF.KeyEventArgs e) => !e.Control && !e.Alt && !e.Shift;

    internal void HandleKeyDown(WF.KeyEventArgs e)
    {
        // The selector is pooled: after Complete() it is parked but alive and still holding
        // keyboard focus, so a key arriving in that window (Escape auto-repeat is the usual
        // way) started tweens and a message dwell on a hidden form, holding the 1 ms clock.
        if (_sessionEnded)
            return;

        if (_mode == SelectorMode.Measure && HandleMeasureKeyDown(e))
            return;

        if (_recordingHud && _pendingScreen is not null && HandleHudKeyDown(e))
            return;

        if (e.KeyCode == WF.Keys.Escape)
        {
            Complete(WF.DialogResult.Cancel);
        }
        else if (e.KeyCode == WF.Keys.Enter && _pendingScreen is SD.Rectangle pending)
        {
            e.Handled = true;
            // With the HUD up, Enter records video and Alt+Enter records a GIF — the two
            // shortcuts the action rows advertise.
            if (_recordingHud)
            {
                e.SuppressKeyPress = true;
                ConfirmRecording(isGif: e.Alt);
                return;
            }
            Confirm(VirtualFromScreen(pending));
        }
        else if (_pendingScreen is SD.Rectangle nudged && SelectionNudge.Handles(e.KeyCode))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (SelectionNudge.Apply(nudged, e.KeyCode, e.Shift, e.Control, _vs) is SD.Rectangle moved &&
                moved != nudged)
            {
                // A nudged rect is a hand-drawn one: ScrollingFlow re-refines a detector-owned
                // rect back onto the pane, which would silently undo the nudge.
                SelectedByPaneClick = false;
                _pendingScreen = moved;
                // A 1 px move at key-repeat rate must not repaint every monitor; the union of
                // where it was and where it is (plus the handle tolerance) is all that changed.
                InvalidateScreenRect(Inflated(SD.Rectangle.Union(nudged, moved)));
            }
        }
        else if (e.KeyCode == WF.Keys.A && IsBareKey(e) && _pendingScreen is SD.Rectangle toSnap)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            SD.Rectangle snapped = MarqueeEdge.SnapAll(toSnap, _snapEdgesX, _snapEdgesY);
            if (snapped != toSnap)
                GlideSelectionTo(snapped);
            else
                FlashMessage("Nothing to snap to near this selection.");
        }
        else if (e.KeyCode == WF.Keys.C && (IsBareKey(e) || (e.Control && !e.Alt && !e.Shift)))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CopyColorUnderCursor();
        }
        else if (e.KeyCode == WF.Keys.Tab && e.Shift)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CopyTextColorUnderCursor();
        }
        else if (e.KeyCode == WF.Keys.Space && IsBareKey(e) && !CapturingPointer)
        {
            // Not mid-drag: Space is "pan" in most tools, and throwing away a marquee the
            // user is still drawing is not what that reflex asks for.
            e.Handled = true;
            e.SuppressKeyPress = true;
            ToggleWindowMode();
        }
        else if (e.KeyCode == WF.Keys.Menu)
        {
            if (!_altLatched)
            {
                _altLatched = true;
                ToggleMagnifier();
            }
            e.Handled = true;
            e.SuppressKeyPress = true; // no menu-loop ding on a borderless overlay
        }
    }

    internal SelectorMode Mode => _mode;

    /// <summary>
    /// Space flips between drawing a rectangle and picking a whole window, mid-selection.
    /// Window capture used to be a different hotkey chosen before the overlay opened, so
    /// realising "actually I want that whole window" cost an Escape and a second gesture.
    /// The window list is already loaded for both modes, so there is nothing to wait for;
    /// the arriving target's wash ramp is what makes the flip read as a change.
    /// </summary>
    private void ToggleWindowMode()
    {
        if (_mode == SelectorMode.Window)
        {
            _mode = SelectorMode.Area;
            SetHoverWindow(null);
        }
        else
        {
            // An in-progress drag or an adjustable pending rect belongs to the mode being
            // left; carrying either into window mode would leave a marquee nothing can
            // confirm on screen.
            _mode = SelectorMode.Window;
            _dragging = false;
            _dragMoved = false;
            _pendingScreen = null;
            _hoverBarButton = -1;
            _pressedBarButton = -1;
            _barTween?.Dispose();
            _barTween = null;
            Array.Clear(_barHover);
            Array.Clear(_barPress);
            _pressedShutter = false;
            // The selection these three animate is gone as of this keystroke. Every other
            // teardown path drops them explicitly; leaving them running kept the process-wide
            // 1 ms clock raised for up to 140 ms against a rect nobody can see.
            _handleTween?.Dispose();
            _handleTween = null;
            _handleScale = 1d;
            _pillTween?.Dispose();
            _pillTween = null;
            _pillHover = false;
            _pillSwap = 0d;
            _pendingGlideTween?.Dispose();
            _pendingGlideTween = null;
            _pendingGlide.Stop();
            _resizeHandle = -1;
            _movingPending = false;
            _pressedEdge = MarqueeSide.None;
            _hoverEdge = MarqueeSide.None;
            _edgeTween?.Dispose();
            _edgeTween = null;
            _edgeGlow = 0d;
            SetHoverPane(null);
            SetHoverWindow(ResolveWindow(_currentScreen));
            StartFollowMotion(); // window mode tracks the cursor even with both chrome toggles off
        }
        if (_hintHold is null && _hintOpacity > 0d)
        {
            _hintText = SelectorHint.Text(_mode == SelectorMode.Window);
            _hintIsMessage = false;
        }
        Cursor = CursorForCurrent();
        InvalidateAllSurfaces();
    }

    internal void HandleKeyUp(WF.KeyEventArgs e)
    {
        if (_mode == SelectorMode.Measure && HandleMeasureKeyUp(e))
            return;

        if (e.KeyCode == WF.Keys.Menu)
            _altLatched = false;
    }

    internal bool MagnifierVisible => _magnifierOn;

    private void ToggleMagnifier()
    {
        _magnifierOn = !_magnifierOn;
        if (_magnifierOn)
            StartFollowMotion(); // the setting may be off, so the follow tick may not be running
        SD.Point pt = _currentScreen;
        InvalidateScreenRect(new SD.Rectangle(
            pt.X - LoupeBoxHalf, pt.Y - LoupeBoxHalf, LoupeBoxHalf * 2, LoupeBoxHalf * 2));
    }

    internal void HandleMouseDown(WF.MouseEventArgs e)
    {
        // Same guard the key handler carries: a pooled selector is parked-but-alive after
        // Complete(), and only WS_EX_TRANSPARENT keeps mouse messages off it today.
        if (_sessionEnded)
            return;
        SD.Point screen = CursorScreen();
        _currentScreen = screen;

        if (_mode == SelectorMode.Measure)
        {
            HandleMeasureMouseDown(e, screen);
            return;
        }

        if (e.Button == WF.MouseButtons.Right)
        {
            Complete(WF.DialogResult.Cancel);
            return;
        }

        if (e.Button != WF.MouseButtons.Left)
            return;

        if (_mode == SelectorMode.Window)
            return; // window mode confirms on mouse-up

        // A press ends any glide in flight. The rect is painted at the eased position but
        // every hit-test reads the destination, so for the 140 ms of a pad / snap / retarget
        // the handles you can see are not the handles you can grab.
        SettlePendingGlide();

        // Area mode. A fresh press is a fresh provenance: the flag below says the rect came
        // from the detector, and downstream probe refinement is only correct for those.
        SelectedByPaneClick = false;
        _pressScreen = screen;

        // The size pill IS the shutter — it cross-fades to a camera glyph and swaps in the
        // camera cursor, so it reads as a button. It must never be the ground a marquee
        // starts on: a press there with a few pixels of trackpad wobble used to confirm a
        // 4x4 capture of the pill's own corner instead of taking the element it labels.
        if (ShutterPillScreenRect() is SD.Rectangle shutter && shutter.Contains(screen))
        {
            _pressedShutter = true;
            return;
        }

        if (_recordingHud)
        {
            // Any press ends an aspect glide: the tween writes the pending rect every frame,
            // and it must not keep doing that under a drag the user has started.
            StopAspectSnap();
            if (_pendingScreen is SD.Rectangle hudPending && HandleHudMouseDown(hudPending, screen))
                return;
        }

        if (_pendingScreen is SD.Rectangle pending)
        {
            // The HUD replaces the Cancel/Done bar entirely: OnPaint draws one or the other,
            // so hit-testing the bar here as well left an invisible-but-live Cancel/Done
            // wherever the two placers disagreed about "below the selection" — and a phantom
            // Done confirmed with no RecordingChoice, silently abandoning the take.
            if (!_recordingHud)
            {
                // Cancel / Done bar wins over move/resize. The press only arms the button —
                // the action fires on mouse-up over the same button, standard button feel.
                var (barRect, padRect, cancelRect, doneRect) = ActionBarRects(BarAnchor ?? pending);
                if (barRect.Contains(screen))
                {
                    _pressedBarButton = doneRect.Contains(screen) ? 1
                        : cancelRect.Contains(screen) ? 0
                        : padRect.Contains(screen) ? 2 : -1;
                    AnimateBarFills();
                    InvalidateScreenRect(barRect);
                    return; // consume any click on the bar (don't start a new marquee under it)
                }
            }

            if (e.Clicks >= 2 && pending.Contains(screen))
            {
                // Double-clicking the framed region is the same "yes, this one" gesture in
                // both modes; with the HUD up it means the primary action, a video take.
                if (_recordingHud)
                    ConfirmRecording(isGif: false);
                else
                    Confirm(VirtualFromScreen(pending));
                return;
            }

            int handle = SelectionHandles.HitTest(screen, pending, ScaleAt(screen));
            if (handle >= 0)
            {
                // Hand-adjusting a detector rect makes it a hand-drawn one: the reset at the
                // top of this method already dropped the provenance for every fresh press.
                _resizeHandle = handle;
                _adjustStartRect = pending;
                return;
            }

            // Handles win the corners; a press on an edge with no drag behind it snaps that
            // ONE edge onto the nearest real boundary, which is the tedious half of drawing a
            // rectangle by hand. The press only ARMS it — the snap fires on mouse-up over the
            // same edge, like every other button here. Firing on the press teleported an edge
            // up to ReachPx away, under a cursor that had been promising a move, with no undo;
            // and returning here made a drag from the border impossible. A press that travels
            // is still a move, so the move state is armed alongside it.
            MarqueeSide side = MarqueeEdge.HitTest(pending, screen, EdgeToleranceAt(screen));
            if (side != MarqueeSide.None)
            {
                _pressedEdge = side;
                if (pending.Contains(screen))
                {
                    _movingPending = true;
                    _adjustAnchor = screen;
                    _adjustStartRect = pending;
                }
                else
                {
                    // The grab band reaches the same distance OUTSIDE the border as inside,
                    // so a press in those few pixels arms the edge snap but has nothing to
                    // move. Arming nothing else made the whole band dead: no move, no new
                    // marquee, and mouse-up re-hit-tested under a pointer that had travelled
                    // and so did not snap either. A fresh marquee is armed alongside the
                    // snap; whichever the gesture turns out to be, mouse-up picks it.
                    _dragStartScreen = screen;
                    _dragging = true;
                    _dragMoved = false;
                }
                return;
            }

            if (pending.Contains(screen))
            {
                _movingPending = true;
                _adjustAnchor = screen;
                _adjustStartRect = pending;
                return;
            }

            // Clicked outside the pending selection — start drawing a new one.
        }

        _pendingScreen = null;
        ShowHud(false);
        _dragStartScreen = screen;
        _dragging = true;
        _dragMoved = false;
        FadeInstructionOut();
        InvalidateAllSurfaces();
    }

    /// <summary>The adjustable selection, for tests and for the keyboard nudge helper.</summary>
    internal SD.Rectangle? PendingRegion => _pendingScreen;

    /// <summary>Display-only glide when the selection jumps by itself (an edge snapping, a
    /// padding step). The pending rect is the destination from the first frame, so Enter and
    /// Done always commit the final rectangle even mid-glide.</summary>
    private readonly RectangleTween _pendingGlide = new();
    private IDisposable? _pendingGlideTween;

    /// <summary>
    /// The rectangle the Cancel/Done/Pad bar is placed on — the EASED rect while a glide is
    /// in flight, the settled one otherwise. Paint and every hit-test read this one property.
    ///
    /// <see cref="_pendingScreen"/> is the destination from the glide's first frame (so Enter
    /// and Done always commit the final rectangle), but the bar sits <see cref="BarGap"/> px
    /// below the region: placing it off the destination while the region was still easing
    /// drew the bar in one place and answered clicks in another for the whole 140 ms, and a
    /// pointer move mid-glide painted a second copy of it at the destination.
    /// </summary>
    private SD.Rectangle? BarAnchor => _pendingScreen is SD.Rectangle pending
        ? (_pendingGlide.IsActive ? _pendingGlide.Current : pending)
        : null;

    /// <summary>
    /// Ends a selection glide early, leaving the rect at its destination. <see cref="_pendingScreen"/>
    /// is already the destination, so this only stops paint from lagging behind the hit-tests.
    /// </summary>
    private void SettlePendingGlide()
    {
        if (_pendingGlideTween is null && !_pendingGlide.IsActive)
            return;
        _pendingGlideTween?.Dispose();
        _pendingGlideTween = null;
        _pendingGlide.Stop();
        InvalidateAllSurfaces();
    }

    /// <summary>
    /// The edge grab band, in physical px, for the monitor a point is on. Six px is four
    /// logical px at Isaiah's 150 % primary and three at 200 %, which is a band too thin to
    /// land on deliberately.
    /// </summary>
    private int EdgeToleranceAt(SD.Point screen) => Math.Max(
        MarqueeEdge.TolerancePx,
        (int)Math.Round(MarqueeEdge.TolerancePx * ScaleAt(screen)));

    /// <summary>
    /// Eases the hovered edge's accent thickening toward its target. The gesture moves an edge
    /// by up to <see cref="MarqueeEdge.ReachPx"/> with no undo, so the edge has to announce
    /// itself before the press rather than after it.
    /// </summary>
    private void AnimateEdgeGlow()
    {
        double from = _edgeGlow;
        double to = _hoverEdge == MarqueeSide.None ? 0d : 1d;
        _edgeTween?.Dispose();
        _edgeTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                _edgeGlow = from + ((to - from) * progress);
                if (BarAnchor is SD.Rectangle p)
                    InvalidateScreenRect(Inflated(p));
            },
            () => _edgeTween = null);
    }

    private void GlideSelectionTo(SD.Rectangle destination)
    {
        if (_pendingScreen is not SD.Rectangle from || destination == from)
            return;

        // Padding, an edge snap and a re-snap all move the rect without a press, so none of
        // them go through HandleMouseDown's provenance reset. A rect the user reshaped is no
        // longer the one the detector chose.
        SelectedByPaneClick = false;
        _pendingScreen = destination;
        _pendingGlideTween?.Dispose();
        _pendingGlide.Retarget(from, destination);
        SD.Rectangle band = SD.Rectangle.Union(from, destination);
        // The bar rides BELOW the region, outside `band`. Repainting the band alone left it
        // painted at its old spot for the whole glide, so each frame also repaints where the
        // bar was and where it now is.
        SD.Rectangle previousBar = ActionBarRects(from).bar;
        _pendingGlideTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                _pendingGlide.Update((float)progress);
                SD.Rectangle nowBar = ActionBarRects(BarAnchor ?? destination).bar;
                InvalidateScreenRect(Inflated(
                    SD.Rectangle.Union(band, SD.Rectangle.Union(previousBar, nowBar))));
                previousBar = nowBar;
            },
            () =>
            {
                _pendingGlideTween = null;
                _pendingGlide.Stop();
                InvalidateAllSurfaces();
            });
    }

    /// <summary>
    /// Hands the selector an adjustable selection and pops its handles in. Eight dots
    /// appearing at full size in one frame reads as a glitch; growing them over QuickMs
    /// reads as the selection becoming grabbable.
    /// </summary>
    private void SetPendingRegion(SD.Rectangle rect)
    {
        bool isNew = _pendingScreen is null;
        _pendingScreen = rect;
        // A framed selection has its own vocabulary — nudge, resize, re-snap — and the hint
        // used to be suppressed in exactly that state. Only for the sessions the first-run
        // hint runs at all, and never over a message the user just triggered.
        if (isNew && _hintEnabled && !_hintIsMessage && _hintHold is null)
        {
            _hintText = SelectorHint.PendingText;
            FadeHint(arriving: true);
            StartFollowMotion();
        }
        _handleTween?.Dispose();
        _handleScale = 0d;
        _handleTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutBack,
            progress =>
            {
                _handleScale = progress;
                if (_pendingScreen is SD.Rectangle p)
                    InvalidateScreenRect(Inflated(p));
            },
            () => _handleTween = null);
    }

    /// <summary>0..1 growth of the pending selection's handles (overshoots past 1).</summary>
    private double _handleScale = 1d;
    private IDisposable? _handleTween;

    /// <summary>True while the pointer is over the detected element's size pill.</summary>
    private bool _pillHover;
    /// <summary>0..1 cross-fade from the size text to the camera glyph on that pill.</summary>
    private double _pillSwap;
    private IDisposable? _pillTween;

    /// <summary>
    /// The size pill for the currently detected element, in screen pixels — or null when
    /// there is no detected element to label. Paint and hit-test share it so the pill you can
    /// click is exactly the pill you can see.
    /// </summary>
    private SD.Rectangle? ShutterPillScreenRect()
    {
        if (!_paneHover || _pendingScreen is not null || _dragging || _hoverPane is not SD.Rectangle pane)
            return null;
        string text = $"{pane.Width} × {pane.Height}";
        // Clamped to the monitor the element is on, matching how the painted pill is clamped
        // to its surface's client area — otherwise an element against the right or bottom
        // edge hit-tests somewhere the pill is not drawn. Sized at that monitor's scale for
        // the same reason: the pill you can click has to be the pill you can see.
        SD.Rectangle pillMonitor = WF.Screen.FromRectangle(pane).Bounds;
        double pillScale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(pillMonitor);
        return SelectorChrome.SizePillRect(
            pane, SelectorChrome.MeasureLabel(text, pillScale), pillMonitor, pillScale);
    }

    /// <summary>
    /// Cross-fades the pill between its size text and a camera glyph. The capture half of
    /// this already worked — a bare click takes the detected element — but nothing on screen
    /// said the highlighted thing was clickable.
    /// </summary>
    private void SetPillHover(bool hovering)
    {
        if (_pillHover == hovering)
            return;
        _pillHover = hovering;

        double from = _pillSwap;
        double to = hovering ? 1d : 0d;
        _pillTween?.Dispose();
        _pillTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                _pillSwap = from + ((to - from) * progress);
                if (ShutterPillScreenRect() is SD.Rectangle pill)
                    InvalidateScreenRect(Inflated(pill));
            },
            () => _pillTween = null);
    }

    /// <summary>0..1 opacity of the hint/confirmation pill; 0 means it is not on screen.</summary>
    private double _hintOpacity;
    private IDisposable? _hintTween;
    /// <summary>What that pill currently says — the first-run hint, or a copy confirmation.</summary>
    private string _hintText = string.Empty;
    /// <summary>True while that pill is carrying a confirmation or a failure rather than the
    /// first-run hint. The hint is suppressed once a selection is framed (it is about drawing
    /// one, and the action bar is there now); a MESSAGE is the only feedback channel the
    /// selector has, so it is painted in every state — including the states that are the only
    /// way to reach some of the messages at all.</summary>
    private bool _hintIsMessage;
    private IDisposable? _hintHold;

    /// <summary>How long a copy confirmation stays up before it fades.</summary>
    private const int MessageHoldMs = 1200;

    /// <summary>
    /// Puts a one-line confirmation where the hint pill goes, held briefly and then faded.
    /// A toast window would be the wrong surface here: the selector is a topmost full-screen
    /// overlay, so the toast lands behind it.
    ///
    /// The hold is a <see cref="Motion.Delay"/>, not a tween. Run() applies its end state
    /// inline when animations are off, so a tweened hold collapsed the whole sequence —
    /// in, hold, out — inside one keystroke and the pill was never painted at all. Only the
    /// fades are allowed to disappear with the animation setting; the message is not.
    /// </summary>
    private void FlashMessage(string text)
    {
        _hintText = text;
        _hintIsMessage = true;
        FadeHint(arriving: true);
        _hintHold?.Dispose();
        _hintHold = WinShot.Core.Motion.Delay(MessageHoldMs, () =>
        {
            _hintHold = null;
            FadeHint(arriving: false);
        });
    }

    /// <summary>Box, in px, that the darkest-pixel sampler searches around the pointer.</summary>
    private const int TextColorSampleBox = 20;

    /// <summary>
    /// Copies the colour under the pointer as hex — or, when a region is framed, that region's
    /// AVERAGE colour. The loupe has always shown the single-pixel value and there was no way
    /// to take it, and off a gradient, a photo or an antialiased logo the single pixel is the
    /// wrong answer anyway.
    /// </summary>
    /// <summary>
    /// Why a colour cannot be sampled yet. The freeze is deferred on purpose (WGC setup alone
    /// can cost 300–1700 ms under load), so <c>_frozen</c> is null for the first fraction of
    /// every session even with the setting already on — sending that user to a checkbox that
    /// is already ticked is the wrong answer to the wrong question.
    /// </summary>
    private string NoFrozenScreenMessage => _options.FreezeScreen
        ? "Still capturing the screen — try again in a moment."
        : "Color needs the frozen screen; turn on Freeze screen in Settings.";

    private void CopyColorUnderCursor()
    {
        if (_frozen is null)
        {
            FlashMessage(NoFrozenScreenMessage);
            return;
        }

        if (ColorSampleRegion() is SD.Rectangle region)
        {
            CopyColor(WinShot.Editor.EyedropperSampler.Average(_frozen, FrozenRect(region)), "average");
            return;
        }

        CopyColor(WinShot.Editor.EyedropperSampler.SampleAt(_frozen, FrozenPoint(_currentScreen)));
    }

    /// <summary>
    /// Copies the darkest pixel in a small box around the pointer — the colour of a line of
    /// type, without having to land the cursor on a glyph stroke, which at 9 pt is a coin toss.
    /// </summary>
    private void CopyTextColorUnderCursor()
    {
        if (_frozen is null)
        {
            FlashMessage(NoFrozenScreenMessage);
            return;
        }

        CopyColor(
            WinShot.Editor.EyedropperSampler.Darkest(_frozen, FrozenPoint(_currentScreen), TextColorSampleBox),
            "text");
    }

    /// <summary>The framed region C should average, or null when nothing is framed and the
    /// single pixel under the pointer is the answer.</summary>
    internal SD.Rectangle? ColorSampleRegion()
    {
        if (_pendingScreen is SD.Rectangle pending)
            return pending;
        if (_dragging && _dragMoved)
            return DragRect();
        if (_mode == SelectorMode.Window && _hoverWindow is not null)
            return _hoverWindow.Bounds;
        if (_paneHover && _hoverPane is SD.Rectangle pane)
            return pane;
        return null;
    }

    /// <summary>
    /// Copies a sampled colour as hex and says what happened. The write is awaited rather
    /// than dropped: a clipboard held open by another process used to fault an unobserved
    /// Task behind a confident "Copied #RRGGBB", so the one action with no other feedback
    /// channel reported success for a paste that would return the previous contents.
    /// </summary>
    private async void CopyColor(SD.Color color, string? kind = null)
    {
        // The selector is POOLED: Complete() parks it, it is not disposed, so IsDisposed is
        // never true and a retrying clipboard write (up to 300 ms) that landed after Escape
        // started a tween and a 1.2 s hold on a hidden window — and could paint a stale
        // "Copied #RRGGBB" over the NEXT session. Guard on the session, as the freeze does.
        TaskCompletionSource<WF.DialogResult>? session = _completion;
        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        try
        {
            await CaptureService.SetTextToClipboardAsync(hex).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("Copying a sampled colour to the clipboard failed", ex);
            if (IsLiveSession(session))
                FlashMessage(FriendlyError.For(ex));
            return;
        }

        if (IsLiveSession(session))
            FlashMessage(kind is null ? $"Copied {hex}" : $"Copied {kind} color {hex}");
    }

    /// <summary>Whether the session an async continuation started in is still the open one.</summary>
    private bool IsLiveSession(TaskCompletionSource<WF.DialogResult>? session) =>
        !IsDisposed && session is not null && _completion == session && !session.Task.IsCompleted;

    /// <summary>A screen rectangle as an offset into the frozen virtual-desktop snapshot.</summary>
    private SD.Rectangle FrozenRect(SD.Rectangle screen) =>
        new(screen.X - _vs.X, screen.Y - _vs.Y, screen.Width, screen.Height);

    /// <summary>A screen point as an offset into the frozen virtual-desktop snapshot.</summary>
    private SD.Point FrozenPoint(SD.Point screen) => new(screen.X - _vs.X, screen.Y - _vs.Y);

    /// <summary>
    /// Fades the hint in when a session opens and out on the first drag. Both directions go
    /// through here so the pill can never be left half-faded by a retarget mid-flight.
    /// </summary>
    private void FadeHint(bool arriving)
    {
        _hintTween?.Dispose();
        _hintTween = null;
        if (!arriving && _hintOpacity <= 0d)
            return;

        double from = _hintOpacity;
        double to = arriving ? 1d : 0d;
        // The hint is drawn on the follow frames, so ShowAsync starts the 8 ms tick for it.
        // Nothing used to stop it, so a user with the crosshair off and the magnifier off —
        // exactly the configuration NeedsCursorFollow exists to detect — paid a full-session
        // cursor-follow tick for the first five sessions after install.
        // Re-evaluated in onDone, never captured: Alt (magnifier) or Space (window mode)
        // arriving DURING the fade starts a follow tick that a decision made 140 ms earlier
        // would then stop, and the loupe stops tracking an idle cursor.
        bool ReleaseFollowTick() => !arriving && !_options.NeedsCursorFollow && !_paneHover &&
            !_magnifierOn && _mode != SelectorMode.Window;
        _hintTween = WinShot.Core.Motion.Run(
            arriving ? WinShot.Core.Motion.EnterDurationMs : WinShot.Core.Motion.QuickMs,
            arriving ? WinShot.Core.Motion.EaseOutCubic : WinShot.Core.Motion.EaseInCubic,
            progress =>
            {
                _hintOpacity = from + ((to - from) * progress);
                InvalidateHintBand();
            },
            () =>
            {
                _hintTween = null;
                // A message that has faded is over. Leaving the flag set meant one C press
                // early in a session permanently ate the framed-selection hint, which is the
                // only place the nudge, [ / ] and A gestures are advertised.
                if (!arriving && _hintOpacity <= 0d)
                    _hintIsMessage = false;
                if (ReleaseFollowTick() && _hintOpacity <= 0d)
                    StopFollowMotion();
            });
    }

    /// <summary>
    /// Where the pill goes on this surface: its usual spot, or pushed below the action bar
    /// when the two would overlap. A confirmation that lands on top of Cancel/Done covers the
    /// controls it is confirming, which is worse than showing it a little lower.
    /// </summary>
    private int? MessagePillTop(SD.Size clientSize, SD.Rectangle monitorBounds)
    {
        if (_pendingScreen is not SD.Rectangle pending)
            return null;
        SD.Rectangle pill = SelectorChrome.HintPillRect(
            clientSize, _hintText, WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));
        var (bar, _, _, _) = ActionBarRects(BarAnchor ?? pending);
        SD.Rectangle barLocal = ToLocal(bar, monitorBounds);
        if (!pill.IntersectsWith(barLocal))
            return null;
        int below = barLocal.Bottom + 8;
        return below + pill.Height <= clientSize.Height ? below : Math.Max(0, barLocal.Top - 8 - pill.Height);
    }

    /// <summary>Repaints only the strip the hint pill occupies, on every surface.</summary>
    private void InvalidateHintBand()
    {
        foreach (SD.Rectangle monitor in HintMonitors())
        {
            InvalidateScreenRect(new SD.Rectangle(
                monitor.Left, monitor.Top + (monitor.Height / 8) - 8, monitor.Width, 64));
        }

        // A message shown over a framed selection dodges the action bar, so the strip it can
        // land in is around the bar, not one eighth down.
        if (BarAnchor is SD.Rectangle pending)
        {
            var (bar, _, _, _) = ActionBarRects(pending);
            InvalidateScreenRect(new SD.Rectangle(
                bar.Left - 200, bar.Top - 64, bar.Width + 400, bar.Height + 128));
        }
    }

    private IEnumerable<SD.Rectangle> HintMonitors()
    {
        yield return _monitorBounds;
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed)
                yield return pane.MonitorBounds;
        }
    }
    /// <summary>
    /// Retires the instruction pill when the drag it was asking for begins.
    /// </summary>
    private void FadeInstructionOut() => RampInstruction(0d);

    /// <summary>Brings the instruction pill up as the selector opens, mirroring
    /// <see cref="FadeInstructionOut"/>.</summary>
    private void FadeInstructionIn() => RampInstruction(1d);

    /// <summary>
    /// Ramps the instruction pill's alpha from wherever it currently is to
    /// <paramref name="target"/>. Always ramps from the live value and always cancels
    /// whatever was in flight: a drag begun inside the entrance tween reverses it instead of
    /// being ignored, and with animations off Motion.Run settles inline. A live handle is
    /// never used as a "a ramp is running" test — with animations off Motion.Run invokes the
    /// completion inline, before the assignment, so such a handle is never null again.
    /// The follow tick is already repainting around the cursor at ~66 fps while a selection
    /// is open, so a frame only has to move the value and ask for the pill's own rect back.
    /// </summary>
    private void RampInstruction(double target)
    {
        _instructionFade?.Dispose();
        _instructionFade = null;
        if (_options.Instruction is null)
        {
            _instructionAlpha = 0d;
            return;
        }

        double from = _instructionAlpha;
        if (Math.Abs(target - from) < 0.001d)
        {
            _instructionAlpha = target;
            InvalidateInstructionRegion();
            return;
        }

        _instructionFade = Core.Motion.Run(Core.Motion.QuickMs,
            target > from ? Core.Motion.EaseOutCubic : Core.Motion.EaseInCubic,
            p =>
            {
                _instructionAlpha = from + ((target - from) * p);
                InvalidateInstructionRegion();
            });
    }

    /// <summary>Screen rect the instruction pill occupies, so the fade repaints only it.</summary>
    private void InvalidateInstructionRegion()
    {
        if (InstructionRectAt(_currentScreen) is SD.Rectangle rect)
            InvalidateScreenRect(rect);
    }

    /// <summary>The pill's screen rect for a cursor at <paramref name="pt"/>, padded, or null
    /// when there is no instruction to draw. The follow repaint needs it too: the pill rides
    /// well outside the crosshair band, so without it a moving cursor would smear the pill.</summary>
    private SD.Rectangle? InstructionRectAt(SD.Point pt)
    {
        if (_options.Instruction is not string instruction)
            return null;
        SD.Size pill = SelectorChrome.MeasureLabel(instruction, ScaleAt(pt));
        return new SD.Rectangle(
            pt.X - (pill.Width / 2) - 4,
            pt.Y - InstructionRise(pill.Height) - pill.Height - 4,
            pill.Width + 8,
            pill.Height + 8);
    }

    /// <summary>
    /// How far above the cursor the instruction pill rides. Derived from the flipped loupe's
    /// own bottom edge rather than fixed: the loupe is a constant 120 device px sitting
    /// FlipY above the cursor, but the pill's height comes from a point-sized font and so
    /// grows with monitor DPI. At a fixed 26 the pill walked up into the loupe's bottom frame
    /// at 150 % and 200 % -- and the pill is drawn last, so it clipped the loupe.
    /// </summary>
    private static int InstructionRise(int pillHeight) =>
        Math.Clamp(FastSelectorLoupeLayout.FlipY - 2 - pillHeight, 8, 26);

    internal void HandleMouseMove()
    {
        if (_sessionEnded)
            return;
        _currentScreen = CursorScreen();

        if (_mode == SelectorMode.Measure)
        {
            // A drop-down does not stop WM_TIMER, so the 8 ms follow tick keeps arriving
            // while the menu is up. Its rows already act on a snapshot of where the user
            // right-clicked; the picture holds still with them, or the reticle, the live
            // pill and the contrast readout keep re-reading pixels BEHIND the menu (which is
            // not in the frozen snapshot) as the pointer walks down the rows.
            if (MeasureMenuHandle == IntPtr.Zero)
                HandleMeasureMouseMove();
            return;
        }

        if (_dragging)
        {
            if (!_dragMoved &&
                Math.Abs(_currentScreen.X - _dragStartScreen.X) < DragThresholdPx &&
                Math.Abs(_currentScreen.Y - _dragStartScreen.Y) < DragThresholdPx)
            {
                return;
            }

            if (!_dragMoved)
            {
                _dragMoved = true;
                // A drag armed in the band just outside a framed selection still carries
                // that selection; it replaces it, exactly like a drag started further out.
                _pendingScreen = null;
                // The hint has done its job the moment a drag starts — once, on the
                // transition. Re-running it every move restarted a 140 ms tween per frame,
                // so the fade never finished and each frame cost a timer plus a full-width
                // hint-band invalidate on every monitor for a pill that is not even painted
                // while dragging.
                FadeHint(arriving: false);
            }
            InvalidateAllSurfaces();
            return;
        }

        if (_resizeHandle >= 0)
        {
            _pendingScreen = ResizeRect(_adjustStartRect, _resizeHandle, _currentScreen);
            InvalidateAllSurfaces();
            return;
        }

        if (_movingPending)
        {
            _pendingScreen = MoveRect(_adjustStartRect, _currentScreen.X - _adjustAnchor.X, _currentScreen.Y - _adjustAnchor.Y);
            InvalidateAllSurfaces();
            return;
        }

        if (_recordingHud)
        {
            UpdateHudHover();
            if (_pendingScreen is not null)
                return; // the HUD owns hover while a selection is pending
        }

        // Cancel / Done hover feedback (only the bar rect repaints on a state change).
        if (BarAnchor is SD.Rectangle barPending)
        {
            var (barRect, padRect, cancelRect, doneRect) = ActionBarRects(barPending);
            int hover = doneRect.Contains(_currentScreen) ? 1
                : cancelRect.Contains(_currentScreen) ? 0
                : padRect.Contains(_currentScreen) ? 2 : -1;
            if (hover != _hoverBarButton)
            {
                _hoverBarButton = hover;
                AnimateBarFills();
                InvalidateScreenRect(barRect);
            }

            // Edge affordance. Nothing else on the selection says which pixels answer the
            // click-to-snap gesture, so the edge under the pointer thickens and the cursor
            // becomes a sizing arrow before anything is committed.
            MarqueeSide edge = barRect.Contains(_currentScreen) ||
                    SelectionHandles.HitTest(_currentScreen, barPending, ScaleAt(_currentScreen)) >= 0
                ? MarqueeSide.None
                : MarqueeEdge.HitTest(barPending, _currentScreen, EdgeToleranceAt(_currentScreen));
            if (edge != _hoverEdge)
            {
                _hoverEdge = edge;
                AnimateEdgeGlow();
            }
        }
        else if (_hoverEdge != MarqueeSide.None)
        {
            _hoverEdge = MarqueeSide.None;
            AnimateEdgeGlow();
        }

        if (_mode == SelectorMode.Window)
        {
            // Window highlight tracks the cursor. Repaint the highlight only when the
            // TARGET changed — this runs off the 8 ms follow tick, so repainting every
            // monitor per cursor pixel is a full-screen redraw for nothing. The size
            // readout rides the cursor, so its band still repaints every move.
            SetHoverWindow(ResolveWindow(_currentScreen));
            InvalidateFollowRegion(_lastFollowScreen, _currentScreen);
            _lastFollowScreen = _currentScreen;
            return;
        }

        // Element detection is always on, ShareX-style: the rect under the cursor lights up
        // the moment the overlay opens, no modifier to discover. Click it to take it;
        // click-drag instead and the marquee wins — a drawn rect is never second-guessed.
        if (_paneHover && _pendingScreen is null && !_dragging)
        {
            // Moving onto the pill must not re-resolve the element out from under it — the
            // pill sits outside the rect it labels, so re-resolving would swap the highlight
            // the instant you reached for the shutter.
            bool onPill = ShutterPillScreenRect() is SD.Rectangle pill && pill.Contains(_currentScreen);
            SetPillHover(onPill);
            if (!onPill)
                UpdateHoverElement(_currentScreen);
        }
        else if (_pillHover)
        {
            SetPillHover(false);
        }

        // Area mode, idle: keep the crosshair + loupe glued to the cursor. Repaint only the
        // old+new crosshair bands and loupe box (not the whole monitor), so a large external
        // display stays smooth. Skip while adjusting a pending rect (no crosshair then).
        if (_pendingScreen is null)
        {
            InvalidateFollowRegion(_lastFollowScreen, _currentScreen);
            _lastFollowScreen = _currentScreen;
        }
    }

    /// <summary>
    /// Cursor-follow heartbeat (~66 fps) while a selection is open. Repaints when the cursor
    /// moves or the Ctrl state changes (Ctrl gates the crosshair in "command" mode), so the
    /// crosshair/loupe track the cursor even when no hover WM_MOUSEMOVE reaches the overlay.
    /// </summary>
    private void OnFollowTick(object? sender, EventArgs e)
    {
        try
        {
            SD.Point p = CursorScreen();
            // Before anything else: a window that raised itself after the overlay opened
            // (a crash dialog, an always-on-top utility) would otherwise sit on top and
            // swallow every click inside its rect while the highlight below kept tracking.
            SelectorForeground.KeepOnTop(SurfaceHandles(), p);
            bool ctrl = (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control;
            if (p == _currentScreen && ctrl == _lastCtrlDown)
                return;
            _lastCtrlDown = ctrl;
            HandleMouseMove();
        }
        catch (Exception ex)
        {
            // A transient GDI/device error (e.g. an RDP display blip) must not crash the app
            // from the timer callback; the next tick recovers on its own.
            Log.Error("Selector follow-tick failed (non-fatal)", ex);
        }
    }

    /// <summary>Whether the full-bleed crosshair guide lines should be drawn, per the
    /// Screenshots "Crosshair mode" setting (always / only while Ctrl is held / never).</summary>
    private bool CrosshairLinesVisible()
    {
        bool controlPressed = (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control;
        return _options.IsCrosshairVisible(controlPressed);
    }

    /// <summary>Wheel up widens the highlight to the next enclosing candidate, wheel down
    /// narrows it back. The detector is usually right about WHERE and only sometimes right
    /// about HOW MUCH; this makes the second half a one-notch correction.</summary>
    internal void HandleMouseWheel(WF.MouseEventArgs e)
    {
        if (_sessionEnded) return;
        if (_mode == SelectorMode.Measure)
        {
            // The ladder gesture is rebound to the tolerance knob here, and only here.
            HandleMeasureWheel(e);
            return;
        }

        if (!_paneHover || _pendingScreen is not null || _dragging || _hoverLadder.Count == 0)
            return;

        int next = Math.Clamp(_ladderIndex + (e.Delta > 0 ? 1 : -1), 0, _hoverLadder.Count - 1);
        if (next == _ladderIndex)
            return;

        _ladderIndex = next;
        // An explicit choice must not be second-guessed by the next mouse twitch.
        _wheelPinned = _hoverLadder[next];
        SetHoverPane(_hoverLadder[next]);
    }

    internal void HandleMouseUp(WF.MouseEventArgs e)
    {
        if (_sessionEnded || e.Button != WF.MouseButtons.Left)
            return;

        _currentScreen = CursorScreen();

        if (_mode == SelectorMode.Measure)
        {
            _draggingGuide = null;
            return;
        }

        if (_mode == SelectorMode.Window)
        {
            if (_hoverWindow is not null)
                Confirm(VirtualFromScreen(_hoverWindow.Bounds), _hoverWindow.Handle);
            return;
        }

        if (_recordingHud && _hudPressed is RecordingHudCell pressedCell)
        {
            _hudPressed = null;
            _hudPressWash?.To(null);
            // Same escape hatch every button on Windows offers: press, drag off, release —
            // nothing happens. Only a release still over the pressed cell commits.
            if (_pendingScreen is SD.Rectangle pressRegion &&
                HudGeometry(pressRegion).HitTest(_currentScreen) == pressedCell)
            {
                HandleHudClick(pressedCell);
            }
            else
            {
                InvalidateHud();
            }
            return;
        }

        if (_pressedBarButton >= 0)
        {
            int pressed = _pressedBarButton;
            _pressedBarButton = -1;
            AnimateBarFills();
            if (_pendingScreen is SD.Rectangle pend)
            {
                var (bar, padButton, cancel, done) = ActionBarRects(BarAnchor ?? pend);
                InvalidateScreenRect(bar);
                if (pressed == 1 && done.Contains(_currentScreen)) Confirm(VirtualFromScreen(pend));
                else if (pressed == 0 && cancel.Contains(_currentScreen)) Complete(WF.DialogResult.Cancel);
                else if (pressed == 2 && padButton.Contains(_currentScreen))
                {
                    // Pad returns the rect unchanged when the padded one would leave the
                    // desktop, and GlideSelectionTo no-ops on that — so a selection already
                    // flush with an edge swallowed the press after playing its press fill.
                    SD.Rectangle padded = MarqueeEdge.Pad(pend, MarqueeEdge.PaddingStepPx, _vs);
                    if (padded != pend)
                        GlideSelectionTo(padded);
                    else
                        FlashMessage("The selection already reaches the edge of the screen.");
                }
            }
            return;
        }

        if (_pressedShutter)
        {
            _pressedShutter = false;
            // Released on the pill it was pressed on: take the element the pill labels.
            if (ShutterPillScreenRect() is SD.Rectangle shutter &&
                shutter.Contains(_currentScreen) &&
                _hoverPane is SD.Rectangle shot)
            {
                SelectedByPaneClick = true;
                Confirm(VirtualFromScreen(shot));
            }
            return;
        }

        // Ctrl+click retargets to the detected element. This has to settle at RELEASE, not
        // at press: staging the element on mouse-down consumed the press, so Ctrl+DRAG — the
        // documented per-drag override of "Adjust selection before capturing", and what the
        // Settings hint promises — could never start over a detected element, which is
        // almost anywhere on screen. A press that travelled is a drag and Ctrl means adjust;
        // a press that stayed put is a click and Ctrl means retarget.
        if (_pressedEdge != MarqueeSide.None)
        {
            MarqueeSide pressed = _pressedEdge;
            _pressedEdge = MarqueeSide.None;
            if (!HasTravelled(_currentScreen))
            {
                // A press that stayed put is the click-one-edge gesture.
                _movingPending = false;
                _dragging = false;
                _dragMoved = false;
                if (_pendingScreen is SD.Rectangle onEdge &&
                    MarqueeEdge.HitTest(onEdge, _currentScreen, EdgeToleranceAt(_currentScreen)) == pressed)
                {
                    SD.Rectangle snapped = MarqueeEdge.SnapEdge(onEdge, pressed, _snapEdgesX, _snapEdgesY);
                    if (snapped != onEdge)
                        GlideSelectionTo(snapped);
                    else
                        FlashMessage("Nothing on screen to line that edge up with.");
                }
                InvalidateAllSurfaces();
                return;
            }

            // It travelled, so it was a move of the selection body or a new marquee drawn
            // from just outside the border. Both are settled by the paths below.
        }

        // A Ctrl-held press that grabbed a handle, an edge or the selection body is an
        // adjustment, not a retarget: releasing it without travelling used to throw the
        // selection away and replace it with whatever sat under the pointer.
        if (_paneHover &&
            (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control &&
            _resizeHandle < 0 && !_movingPending &&
            !HasTravelled(_currentScreen))
        {
            _dragging = false;
            _dragMoved = false;
            _resizeHandle = -1;
            _movingPending = false;
            if (SnapRectAt(_currentScreen) is not SD.Rectangle element)
            {
                // The other three retargets pair success with a failure line — A, an edge
                // press, Pad edges. Ctrl+click is the least discoverable of the four, so
                // failing mute read as the modifier simply not being wired at all.
                FlashMessage("Nothing under the pointer to snap to.");
                InvalidateAllSurfaces();
                return;
            }
            if (_pendingScreen is null)
            {
                SetPendingRegion(element);
                InvalidateAllSurfaces();
            }
            else
            {
                GlideSelectionTo(element);
            }
            SelectedByPaneClick = true; // set last: GlideSelectionTo clears the provenance
            return;
        }

        if (_resizeHandle >= 0 || _movingPending)
        {
            // A fresh marquee is dropped when it collapses; a resize cannot be, because the
            // user already has a selection on screen. Dragging a handle onto its own anchor
            // otherwise left a 0x0 pending rect that still looked framed and made Record a
            // silent no-op, so the release grows it back to something usable instead.
            if (_resizeHandle >= 0 && _pendingScreen is SD.Rectangle resized)
                _pendingScreen = EnsureUsablePending(resized);
            _resizeHandle = -1;
            _movingPending = false;
            InvalidateAllSurfaces();
            return;
        }

        if (!_dragging)
            return;

        _dragging = false;
        if (_dragMoved)
        {
            // Don't capture yet — present an adjustable selection (move/resize, then Enter or
            // double-click to confirm), matching CleanShot's behavior.
            SD.Rectangle rect = DragRect();
            rect.Intersect(_vs);
            bool controlPressed = (WF.Control.ModifierKeys & WF.Keys.Control) == WF.Keys.Control;
            // Never with the HUD up: the take's options still have to be chosen, and the HUD
            // is where they live. Mouse-up-confirm belongs to the screenshot flows, which have
            // nothing left to ask once the rectangle is drawn.
            if (!_recordingHud && rect.Width >= MinConfirmDragPx && rect.Height >= MinConfirmDragPx &&
                !_options.ShouldAdjustSelection(controlPressed))
            {
                // Releasing takes the shot. Every region capture used to cost a second
                // gesture — drag, then aim at Done or reach for Enter — for an adjustment
                // most captures never wanted; the adjustable path is now the opt-in.
                Confirm(VirtualFromScreen(rect));
                return;
            }

            if (rect.Width > 0 && rect.Height > 0)
            {
                SetPendingRegion(rect);
                ShowHud(true);
            }
            InvalidateAllSurfaces();
        }
        else if (_recordingHud && _pendingScreen is null && _hoverPane is SD.Rectangle hudPane)
        {
            // With the HUD up a window click frames the take rather than starting it — the
            // options still have to be chosen, and the HUD is where they live. No
            // SelectedByPaneClick: nothing is being confirmed yet, and that flag opts the
            // result into probe refinement downstream.
            _pendingScreen = hudPane;
            ShowHud(true);
            FadeHint(arriving: false);
            InvalidateAllSurfaces();
        }
        else if (_paneHover && _pendingScreen is null && _hoverPane is SD.Rectangle pane)
        {
            // Bare click on the highlighted pane accepts it (CleanShot's scrolling flow).
            SelectedByPaneClick = true;
            Confirm(VirtualFromScreen(pane));
        }
    }

    // ----------------------------------------------------------- painting (per surface)

    /// <summary>
    /// True when PaintSurface's own background covers every pixel, so the surface does not
    /// need clearing first. Without a frozen snapshot it paints a SEMI-transparent dim that
    /// relies on the black underneath, so the Clear is still required there.
    /// </summary>
    internal bool HasFullSurfaceBackground => _frozen is not null;

    /// <summary>How far the overlay darkens the desktop behind the selection. One value for
    /// the baked frozen slice and for the live fallback, so the two can never drift apart.</summary>
    private const int OverlayDimAlpha = 115;

    /// <summary>
    /// The surface's ground: the frozen desktop slice dimmed and baked once, blitted as one
    /// opaque copy — or, when the snapshot never arrived, a plain dim of the same weight.
    /// Every mode paints its ground through here so a change to the dim reaches all of them.
    /// </summary>
    private void PaintFrozenOrDim(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        // Only the invalid region. Every repaint in Measure mode is a band — the 8 ms follow
        // tick asks for one on every tick — and blitting a whole 4K monitor to fill a 240 px
        // band was ~20 ms a frame of pure waste. Outside the clip nothing lands on screen, so
        // this changes what is drawn and not what is seen.
        SD.Rectangle dest = ClipRect(g, monitorBounds.Size);
        if (dest.Width <= 0 || dest.Height <= 0)
            return;

        var dimmed = GetDimmedBackground(monitorBounds);
        if (dimmed is not null)
        {
            // SourceCopy: the background covers the whole surface, so there is nothing to
            // blend with and a straight copy is a lot cheaper at full-monitor size.
            var previousCompositing = g.CompositingMode;
            g.CompositingMode = SD.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(dimmed, dest, dest.X, dest.Y, dest.Width, dest.Height, SD.GraphicsUnit.Pixel);
            g.CompositingMode = previousCompositing;
            return;
        }

        using var dim = new SD.SolidBrush(SD.Color.FromArgb(OverlayDimAlpha, 0, 0, 0));
        g.FillRectangle(dim, dest);
    }

    internal void PaintSurface(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
        SD.Size clientSize = monitorBounds.Size;
        bool cursorOnThisSurface = monitorBounds.Contains(_currentScreen);
        // THIS surface's monitor scale. Every pill measures and draws through it, so the
        // chrome is sized for the display it lands on and not for the primary.
        double s = WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds);

        if (_mode == SelectorMode.Measure)
        {
            PaintMeasureSurface(g, monitorBounds, cursorOnThisSurface);
            return;
        }

        // Advance the glide on the paint clock, not on a timer tick. Update() reads a
        // stopwatch, so calling it once per surface in a multi-monitor frame is harmless.
        SD.Rectangle tweenBefore = _hoverTween.Current;
        bool tweening = _hoverTween.IsActive && _hoverTween.Update();

        PaintFrozenOrDim(g, monitorBounds);

        // The active selection (drawing / adjustable pending / hovered window) shows the
        // frozen pixels at full brightness with an accent border.
        SD.Rectangle? brightScreen = null;
        SD.Rectangle? labelScreen = null;
        bool showHandles = false;
        if (_dragging && _dragMoved)
        {
            brightScreen = DragRect();
        }
        else if (_pendingScreen is SD.Rectangle pending)
        {
            brightScreen = _pendingGlide.IsActive ? _pendingGlide.Current : pending;
            showHandles = true;
        }
        else if (_mode == SelectorMode.Window && _hoverWindow is not null)
        {
            brightScreen = _hoverWindow.Bounds;
        }
        else if (_paneHover && _hoverPane is SD.Rectangle hoverPane)
        {
            // Mid-glide the border rides the interpolated rect, but the readout stays on the
            // real one - spinning digits for 200 ms reads as a glitch, not as motion.
            brightScreen = tweening ? _hoverTween.Current : hoverPane;
            labelScreen = hoverPane;
        }

        if (brightScreen is SD.Rectangle bright)
        {
            var local = ToLocal(bright, monitorBounds);
            BrightenRegion(g, monitorBounds, local);
            // Scaled like the edge-press glow below, which starts at this same 2 px weight:
            // an unscaled base made hovering an edge pop the stroke a pixel wider at 150 %
            // before the tween had done anything.
            using (var pen = new SD.Pen(Accent, (float)(2d * Math.Max(1d, s))))
                g.DrawRectangle(pen, local);

            // The edge under the pointer thickens: the only thing on the selection that says
            // "clicking here moves this one edge onto the nearest boundary".
            if (showHandles && _hoverEdge != MarqueeSide.None && _edgeGlow > 0.01d)
            {
                float weight = (float)((2d + (4d * Math.Clamp(_edgeGlow, 0d, 1d))) * Math.Max(1d, s));
                using var edgePen = new SD.Pen(Accent, weight);
                switch (_hoverEdge)
                {
                    case MarqueeSide.Left:
                        g.DrawLine(edgePen, local.Left, local.Top, local.Left, local.Bottom);
                        break;
                    case MarqueeSide.Right:
                        g.DrawLine(edgePen, local.Right, local.Top, local.Right, local.Bottom);
                        break;
                    case MarqueeSide.Top:
                        g.DrawLine(edgePen, local.Left, local.Top, local.Right, local.Top);
                        break;
                    default:
                        g.DrawLine(edgePen, local.Left, local.Bottom, local.Right, local.Bottom);
                        break;
                }
            }

            if (_mode == SelectorMode.Window && _hoverWindow is not null && _pendingScreen is null && !_dragging)
            {
                // A hand-drawn marquee and a whole window looked identical — both a plain
                // accent border over undimmed pixels. The wash says "this is one object,
                // and it is the thing that will be captured".
                int washAlpha = (int)Math.Round(WindowWashAlpha * Math.Clamp(_hoverWash, 0d, 1d));
                if (washAlpha > 0)
                {
                    using var wash = new SD.SolidBrush(SD.Color.FromArgb(washAlpha, Accent));
                    g.FillRectangle(wash, local);
                }
                // One pill per window, not one per monitor the window overlaps: this block
                // runs on every surface, so a window spanning two displays used to draw its
                // name twice, each centred in that display's slice of it.
                if (_hoverAppName.Length > 0 && cursorOnThisSurface)
                {
                    SD.Size pill = SelectorChrome.MeasureLabel(_hoverAppName, s);
                    SelectorChrome.DrawLabel(g, clientSize, _hoverAppName,
                        local.Left + (local.Width - pill.Width) / 2,
                        local.Top + (local.Height - pill.Height) / 2, s,
                        // Rides the wash it sits on: landing solid over a tint still ramping
                        // is the same pop the wash exists to avoid.
                        Math.Clamp(_hoverWash, 0d, 1d));
                }
            }

            if (showHandles)
                DrawHandles(g, monitorBounds, bright);

            if (cursorOnThisSurface)
            {
                SD.Rectangle sized = labelScreen ?? bright;
                // The readout is placed off the rect it LABELS, never off the interpolated
                // border. `local` rides _hoverTween mid-retarget while ShutterPillScreenRect()
                // — the thing a press is tested against — always uses the settled element, so
                // deriving the position from `local` painted the shutter somewhere it could
                // not be clicked for the whole 200 ms glide.
                SD.Rectangle labelLocal = ToLocal(sized, monitorBounds);
                string sizeText = $"{sized.Width} × {sized.Height}";
                int pillGap = WinShot.Recording.RecordingMonitorDpi.Px(8, s);
                SD.Point at = new(labelLocal.Right + pillGap, labelLocal.Bottom + pillGap);
                if (_mode == SelectorMode.Window && _hoverWindow is not null && _pendingScreen is null && !_dragging)
                {
                    SD.Point atCursor = ToLocal(_currentScreen, monitorBounds);
                    SD.Size follow = SelectorChrome.ReadoutOffset(s);
                    at = new SD.Point(atCursor.X + follow.Width, atCursor.Y + follow.Height);
                }
                else if (_pendingScreen is not null)
                {
                    // The Cancel/Done bar owns the space below a pending region — on a narrow
                    // selection the pill would land on Cancel — so the readout rides the
                    // selection's top-right, flipped just inside when the region touches the
                    // monitor top.
                    SD.Size pill = SelectorChrome.MeasureLabel(sizeText, s);
                    int pillTop = labelLocal.Top - pill.Height - pillGap;
                    if (pillTop < 0) pillTop = labelLocal.Top + pillGap;
                    at = new SD.Point(Math.Max(0, labelLocal.Right - pill.Width), pillTop);
                }
                if (labelScreen is not null)
                    SelectorChrome.DrawShutterPill(g, clientSize, sizeText, at, _pillSwap, s);
                else
                    SelectorChrome.DrawLabel(g, clientSize, sizeText, at.X, at.Y, s);
            }
        }

        // Ask for the next frame. Self-sustaining for the 200 ms of the glide, then it stops
        // on its own because Update() clears IsActive once the stopwatch passes the duration.
        if (tweenBefore != _hoverTween.Current)
            InvalidateHoverRegion(tweenBefore, _hoverTween.Current);

        // Cancel / Done bar on the adjustable selection: below the region, flipped above when it
        // runs to the screen bottom, tucked inside the bottom when the region spans the full height.
        if (_recordingHud)
        {
            // The exit tween outlives the pending selection, so it keeps painting against the
            // region the HUD was last anchored to.
            if ((_pendingScreen ?? _hudLastRegion) is SD.Rectangle hudPending)
                DrawRecordingHud(g, monitorBounds, hudPending);
        }
        else if (BarAnchor is SD.Rectangle pendBar)
        {
            var (bar, padButton, cancel, done) = ActionBarRects(pendBar);
            if (bar.IntersectsWith(monitorBounds))
                DrawActionBar(g, monitorBounds, bar, padButton, cancel, done);
        }

        // Opacity is the only gate. A `!_dragging` gate unpainted the pill on the mouse-DOWN,
        // one event before the 140 ms exit fade starts on the first move -- so the entrance
        // was answered by a hard cut and the fade animated a value nobody could see. Worse, a
        // press that never travelled (the bare element click, now the common gesture) cleared
        // _dragging on release and blinked the pill straight back at full strength.
        if (_hintOpacity > 0d && cursorOnThisSurface)
        {
            SelectorChrome.DrawHint(
                g, clientSize, _hintText, _hintOpacity, s, MessagePillTop(clientSize, monitorBounds));
        }

        // Crosshair + loupe only when drawing/idle in Area mode (not while adjusting a pending
        // rect, and never in Window mode where the window highlight is the affordance).
        bool inCrosshairContext = _mode == SelectorMode.Area && _pendingScreen is null && cursorOnThisSurface;
        if (inCrosshairContext)
        {
            // Crosshair guide lines honor the "Crosshair mode" setting; the magnifier/color
            // loupe honors "Show magnifier". Both default on when no settings are attached.
            if (CrosshairLinesVisible())
                SelectorChrome.DrawCrosshair(g, clientSize, ToLocal(_currentScreen, monitorBounds), s);

            // While dragging, the readout says how big the selection is; idle, where the
            // pointer is. The loupe carries it when the magnifier is on, and the crosshair
            // carries it on its own when the magnifier is off — either way the selector
            // always answers "where am I".
            SD.Size dragSize = _dragging && _dragMoved
                ? DragRect().Size
                : SD.Size.Empty;
            if (_magnifierOn)
            {
                FastSelectorLoupeRenderer.Draw(
                    g, clientSize, _vs, ToLocal(_currentScreen, monitorBounds), _currentScreen, _frozen, dragSize);
            }
            else if (dragSize.IsEmpty)
            {
                // Mid-drag the selection's own size pill already sits at the corner the
                // cursor is on; a second copy 16 px away is the same number twice.
                SelectorChrome.DrawCursorReadout(g, clientSize,
                    SelectorReadout.Text(_currentScreen, dragSize), ToLocal(_currentScreen, monitorBounds), s);
            }
        }

        // Mode-specific instruction, riding above the cursor so it never lands on the loupe.
        // Drawn last so nothing paints over it, and only while there is no selection yet --
        // once a region exists the size readout and the action bar own the copy.
        if (_options.Instruction is string instruction && _instructionAlpha > 0.004d &&
            cursorOnThisSurface && _pendingScreen is null)
        {
            SD.Point cursorLocal = ToLocal(_currentScreen, monitorBounds);
            SD.Size pill = SelectorChrome.MeasureLabel(instruction, s);
            SelectorChrome.DrawLabel(g, clientSize, instruction,
                cursorLocal.X - (pill.Width / 2),
                cursorLocal.Y - InstructionRise(pill.Height) - pill.Height,
                s,
                _instructionAlpha);
        }
    }

    /// <summary>
    /// Returns this monitor's frozen slice with the selection dim baked in, building and
    /// caching it on first use. Lets the hot follow-paint be one opaque blit instead of a
    /// per-frame snapshot crop plus a full-screen software alpha fill.
    /// </summary>
    private SD.Bitmap? GetDimmedBackground(SD.Rectangle monitorBounds)
    {
        if (_frozen is null) return null;
        if (_dimmedCache.TryGetValue(monitorBounds, out var cached))
            return cached;

        // Format32bppRgb, not PArgb: the result is opaque, and an alpha channel would force
        // GDI+ to blend this full-monitor bitmap per pixel on every single paint.
        var bmp = new SD.Bitmap(monitorBounds.Width, monitorBounds.Height, SD.Imaging.PixelFormat.Format32bppRgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;
            DrawFrozenSlice(g, monitorBounds);
            using var dim = new SD.SolidBrush(SD.Color.FromArgb(OverlayDimAlpha, 0, 0, 0));
            g.FillRectangle(dim, 0, 0, monitorBounds.Width, monitorBounds.Height);
        }
        _dimmedCache[monitorBounds] = bmp;
        return bmp;
    }

    /// <summary>Paints this monitor's slice of the frozen snapshot at 1:1 (pure offset).</summary>
    private void DrawFrozenSlice(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        if (_frozen is null) return;
        var src = new SD.Rectangle(monitorBounds.X - _vs.X, monitorBounds.Y - _vs.Y, monitorBounds.Width, monitorBounds.Height);
        g.DrawImage(_frozen, new SD.Rectangle(0, 0, monitorBounds.Width, monitorBounds.Height), src, SD.GraphicsUnit.Pixel);
    }

    /// <summary>Re-draws the frozen slice clipped to a region so it shows undimmed.</summary>
    private void BrightenRegion(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle localRect)
    {
        if (_frozen is null) return;
        var clip = SD.Rectangle.Intersect(localRect, new SD.Rectangle(0, 0, monitorBounds.Width, monitorBounds.Height));
        if (clip.Width < 1 || clip.Height < 1) return;

        var state = g.Save();
        // Intersect, never Replace: SetClip(Rectangle) defaults to CombineMode.Replace, which
        // throws away the update region the paint was asked for and re-blits the whole run at
        // full brightness. It never reaches the screen (the back buffer is rendered through
        // BeginPaint's clip), but it corrupts the buffer for every caller — and Area and
        // Window mode share this helper.
        g.IntersectClip(clip);
        DrawFrozenSlice(g, monitorBounds);
        g.Restore(state);
    }

    /// <summary>Draws the 8 move/resize handles on the pending selection.</summary>
    private void DrawHandles(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle screenRect) =>
        SelectionHandles.Draw(g, ToLocal(screenRect, monitorBounds), _handleScale, _resizeHandle,
            WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));

    private void DisposeFrozen()
    {
        _frozen?.Dispose();
        _frozen = null;
        foreach (var bmp in _dimmedCache.Values)
            bmp.Dispose();
        _dimmedCache.Clear();
    }

    private SD.Bitmap? CropFrozen(SD.Rectangle virtualRect)
    {
        if (_frozen is null) return null;
        var src = SD.Rectangle.Intersect(virtualRect, new SD.Rectangle(0, 0, _frozen.Width, _frozen.Height));
        if (src.Width < 1 || src.Height < 1) return null;

        var crop = new SD.Bitmap(src.Width, src.Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(crop);
        g.DrawImage(_frozen, new SD.Rectangle(0, 0, src.Width, src.Height), src, SD.GraphicsUnit.Pixel);
        return crop;
    }

    /// <summary>
    /// Cuts the shadowed wedges out of the corners of a window capture, when the setting is on
    /// and that window is rounded. A no-op for a dragged region, a square-cornered window, and
    /// on the live-grab fallback path, which never reaches this crop at all.
    /// </summary>
    private void TrimWindowShadow(IntPtr window, bool wholeWindowCaptured)
    {
        if (WinShot.Core.WindowShadowTrim.TrimWindowCapture(
                _capturedRegion, window,
                _settings?.Current.TrimWindowShadow ?? true,
                wholeWindowCaptured) is SD.Bitmap trimmed)
        {
            _capturedRegion!.Dispose();
            _capturedRegion = trimmed;
        }
    }

    /// <summary>Returns the region cropped from the frozen snapshot at confirm time and transfers
    /// ownership to the caller (null if freeze was unavailable). Null after the first call.</summary>
    public SD.Bitmap? TakeCapturedRegion()
    {
        var bmp = _capturedRegion;
        _capturedRegion = null;
        return bmp;
    }

    /// <summary>
    /// Resolves the window under the cursor by real z-order (WindowFromPoint) so a small
    /// foreground window wins over a larger background one; falls back to the first
    /// bounds-containing window when the topmost hwnd isn't in the cached list.
    /// </summary>
    private WindowInfo? ResolveWindow(SD.Point screenPoint)
    {
        IntPtr top = WindowEnumerator.TopLevelWindowFromPoint(screenPoint);
        if (top != IntPtr.Zero && top != Handle && !IsPaneHandle(top))
        {
            var match = _windows.FirstOrDefault(w => w.Handle == top);
            if (match is not null)
                return match;
        }

        return _windows.FirstOrDefault(w => w.Bounds.Contains(screenPoint));
    }

    /// <summary>
    /// Hover resolution is the FIRST rect in the snap list containing the cursor,
    /// nothing else. The list is EnumWindows + EnumChildWindows + client rects, deepest-first
    /// in z-order, built once before the overlay opened — so the answer is instant, identical
    /// on every pass, and never revised after the fact. Where an app draws its whole UI into
    /// one HWND (Chromium, Electron, WPF) the answer is the window, same as every other
    /// HWND-level selector — and the confidence of never being second-guessed is the feature.
    /// </summary>
    private void UpdateHoverElement(SD.Point point)
    {
        // A wheel-chosen rung is an explicit decision: keep it while the cursor stays inside.
        if (_wheelPinned is SD.Rectangle pinned && pinned.Contains(point))
            return;
        _wheelPinned = null;

        SD.Rectangle? snap = SnapRectAt(point);

        if (!_elementGranularity && snap is null)
        {
            // Scrolling-capture flow: fall back to the scrollable pane / window under the
            // cursor (pre-existing behavior, unrelated to element snapping).
            WindowInfo? win = ResolveWindow(point);
            snap = win is null
                ? null
                : WinShot.Scrolling.ScrollPaneDetector.QuickPaneRect(win.Handle, point) ?? win.Bounds;
        }

        if (snap is SD.Rectangle chosen)
        {
            // The pixel walk goes in as one more rung. Colour delta fails on Win11 mica
            // chrome exactly where the HWND rect is exact, and the HWND rect is useless on
            // a photo or a canvas where the pixel walk is exact - merging them means the
            // wheel walks both kinds of candidate in one list.
            BuildLadder(point, chosen, PixelRungAt(point));
            SetHoverPane(chosen);
        }
    }

    /// <summary>
    /// Collects the nesting of rects under the cursor - the resolved one plus every HWND rect
    /// containing the point - smallest first, so the wheel has somewhere to go in both
    /// directions. Rungs closer than the wobble tolerance are merged; stepping onto a rect
    /// eight pixels bigger is not a step a person can see.
    /// </summary>
    private void BuildLadder(SD.Point point, SD.Rectangle resolved, SD.Rectangle? pixelRung = null)
    {
        var rungs = new List<SD.Rectangle> { resolved };
        foreach (var candidate in _snapRects)
        {
            if (candidate.Bounds.Contains(point))
                rungs.Add(candidate.Bounds);
        }
        if (pixelRung is SD.Rectangle pixels)
            rungs.Add(pixels);

        var ladder = new List<SD.Rectangle>();
        foreach (var rung in rungs.OrderBy(r => (long)r.Width * r.Height))
        {
            if (ladder.Count == 0 || !IsWithin(ladder[^1], rung, WobbleTolerancePx))
                ladder.Add(rung);
        }

        _hoverLadder = ladder;
        _ladderIndex = ladder.FindIndex(r => IsWithin(r, resolved, WobbleTolerancePx));
        if (_ladderIndex < 0)
            _ladderIndex = 0;
    }

    /// <summary>
    /// The one place the hover highlight changes. Routing every tier through here is what
    /// keeps the glide honest: each new rect starts its slide from wherever the highlight
    /// currently sits, so a stale tier answer can never make it jump backwards.
    /// </summary>
    private void SetHoverPane(SD.Rectangle? rect)
    {
        if (rect == _hoverPane)
            return;

        if (_mode == SelectorMode.Measure)
        {
            // A guard rail, not a live path: both callers already return before they reach
            // here in Measure mode. It stands so that the rule survives the next caller —
            // functional data never animates, because an easing measurement reads as
            // imprecise. The glide is untouched for Area and Window, where it is right.
            _hoverTween.Stop();
            InvalidateAllSurfaces();
        }
        else if (_hoverPane is SD.Rectangle previous && rect is SD.Rectangle next)
        {
            _hoverTween.Retarget(previous, next);
            InvalidateHoverRegion(previous, next);
        }
        else
        {
            _hoverTween.Stop();
            InvalidateAllSurfaces();
        }
        _hoverPane = rect;
    }

    /// <summary>
    /// The one place the hovered WINDOW changes. Returns true when the target actually
    /// moved (and the old + new highlights were repainted); false is the common case on a
    /// cursor tick inside the same window, and repainting then is a whole-monitor redraw
    /// that shows nothing new.
    /// </summary>
    internal bool SetHoverWindow(WindowInfo? next)
    {
        if (next == _hoverWindow)
            return false;

        WindowInfo? previous = _hoverWindow;
        _hoverWindow = next;
        _hoverAppName = next is null ? string.Empty : AppNameFor(next);
        BeginHoverWash(next is not null, previous is not null);
        if (previous is not null && next is not null)
            InvalidateHoverRegion(previous.Bounds, next.Bounds);
        else if (previous is not null)
            InvalidateScreenRect(Inflated(previous.Bounds));
        else if (next is not null)
            InvalidateScreenRect(Inflated(next.Bounds));
        return true;
    }

    /// <summary>
    /// The friendly app name for a hovered window, resolved off the UI thread and cached for
    /// the session. <see cref="WindowEnumerator.AppNameFor"/> opens the target process and
    /// reads its PE version resource; this runs from the 8 ms cursor-follow tick, so sweeping
    /// the pointer across a row of windows used to stall the very thread that draws the wash,
    /// once per boundary crossing — and for an elevated window (Task Manager) it threw and
    /// swallowed a Win32Exception on the paint path every time. The title is shown at once and
    /// the friendlier name swaps in when it lands.
    /// </summary>
    private string AppNameFor(WindowInfo window)
    {
        if (_appNames.TryGetValue(window.Handle, out string? cached))
            return cached;

        IntPtr handle = window.Handle;
        string title = window.Title;

        // No context to come back on (a headless test) means no way to touch the cache or the
        // surfaces safely, so the title stands on its own — which is the fallback anyway.
        SynchronizationContext? ui = SynchronizationContext.Current;
        if (ui is null)
            return title;

        _ = Task.Run(() => WindowEnumerator.AppNameFor(handle, title))
            .ContinueWith(
                t => ui.Post(_ => OnAppNameResolved(handle, t.Result), null),
                TaskContinuationOptions.OnlyOnRanToCompletion);

        return title;
    }

    private void OnAppNameResolved(IntPtr handle, string name)
    {
        if (IsDisposed)
            return;
        _appNames[handle] = name;
        // Only repaint if the pointer is still on the window that asked for it.
        if (_hoverWindow is WindowInfo current && current.Handle == handle && _hoverAppName != name)
        {
            _hoverAppName = name;
            InvalidateScreenRect(Inflated(current.Bounds));
        }
    }

    /// <summary>Friendly app names already resolved this session, keyed by window handle.</summary>
    private readonly Dictionary<IntPtr, string> _appNames = new();

    /// <summary>
    /// Ramps the window-target wash in over <see cref="Motion.QuickMs"/> when the tint
    /// arrives from nothing. Retargeting straight from one window to another CARRIES the
    /// wash across at its current strength instead of restarting it at zero — that is what
    /// makes a retarget read as the tint moving rather than one rectangle blinking off while
    /// its neighbour fades up. Only the wash is painted, and only for the window under the
    /// pointer, so leaving has nothing to ramp: the tint goes with the rectangle it was on.
    ///
    /// This is the one asymmetric entrance/exit on the selector, and it only holds while the
    /// accent BORDER is also instant. Give the border a fade and this must get one too, or the
    /// wash will read as a pop against it.
    /// </summary>
    private void BeginHoverWash(bool arriving, bool wasHovering)
    {
        if (arriving && wasHovering && _hoverWashTween is null)
            return; // the tint moves with the target; it is already at full strength

        _hoverWashTween?.Dispose();
        _hoverWashTween = null;
        if (!arriving)
        {
            _hoverWash = 0d;
            return;
        }

        double from = wasHovering ? _hoverWash : 0d;
        _hoverWash = from;
        _hoverWashTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                _hoverWash = from + ((1d - from) * progress);
                if (_hoverWindow is WindowInfo w)
                    InvalidateScreenRect(Inflated(w.Bounds));
            },
            () => _hoverWashTween = null);
    }

    /// <summary>A highlight's ground plus everything drawn outside it: the border pen and
    /// its antialiasing, and -- on a framed selection -- the handle dots, their pop-in
    /// overshoot and the grabbed handle's focus ring. Those scale with the monitor now, so a
    /// fixed 4 px margin sliced the dots flat on anything above 100 %.</summary>
    private SD.Rectangle Inflated(SD.Rectangle rect)
    {
        int margin = SelectionHandles.OutsetAt(ScaleAt(new SD.Point(rect.Left, rect.Top)));
        rect.Inflate(margin, margin);
        return rect;
    }

    /// <summary>Edge-wise nearness: true when every side of the two rects is within
    /// <paramref name="tolerance"/> px.</summary>
    private static bool IsWithin(SD.Rectangle a, SD.Rectangle b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance &&
        Math.Abs(a.Right - b.Right) <= tolerance && Math.Abs(a.Bottom - b.Bottom) <= tolerance;

    /// <summary>Repaints only the ground the highlight covers across a glide step - the
    /// brightened interior changes too, so the whole union has to be redrawn, but that is
    /// still far less than the whole (possibly 4K) monitor.</summary>
    private void InvalidateHoverRegion(SD.Rectangle from, SD.Rectangle to)
    {
        var union = SD.Rectangle.Union(from, to);
        union.Inflate(4, 4); // border pen width + antialiasing
        InvalidateScreenRect(union);
        InvalidateScreenRect(new SD.Rectangle(union.Right, union.Bottom, LoupeBoxHalf, 64)); // size label
    }

    /// <summary>Innermost snappable rect under the point. The list is deepest-first, so the
    /// first hit IS the answer — no area comparison, no per-move enumeration.</summary>
    private SD.Rectangle? SnapRectAt(SD.Point point)
    {
        foreach (var candidate in _snapRects)
        {
            if (candidate.Bounds.Contains(point))
                return candidate.Bounds;
        }
        return null;
    }

    /// <summary>
    /// The dragged corner, pulled onto a real element edge when it comes within a few px of
    /// one. Hand-drawn control, but the result lines up with what is actually on screen -
    /// eyeballing the last three pixels is the tedious part of drawing a rectangle.
    /// </summary>
    private SD.Point SnappedCursor() => new(
        SnapCoordinate(_currentScreen.X, _snapEdgesX),
        SnapCoordinate(_currentScreen.Y, _snapEdgesY));

    internal static int SnapCoordinate(int value, int[] edges)
    {
        if (edges.Length == 0)
            return value;

        int at = Array.BinarySearch(edges, value);
        if (at >= 0)
            return value;

        at = ~at; // first edge greater than value
        int best = value;
        int bestGap = MarqueeSnapPx + 1;
        for (int i = at - 1; i <= at; i++)
        {
            if (i < 0 || i >= edges.Length)
                continue;
            int gap = Math.Abs(edges[i] - value);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = edges[i];
            }
        }
        return best;
    }

    private static int Distance(SD.Point a, SD.Point b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>
    /// Every window handle the selector owns, back to front - the set
    /// <see cref="SelectorForeground.KeepOnTop"/> re-raises together.
    ///
    /// The overlay's own transient chrome goes on the end, above the panes. KeepOnTop treats
    /// any top-level window under the cursor that is not in this list as an intruder to be
    /// buried, and Measure mode is the first surface in the app to raise a toast and open a
    /// drop-down OVER its own live overlay: without them here, parking the pointer on a
    /// measure toast, or simply opening the right-click menu (which appears under the
    /// pointer by definition), makes the next 8 ms follow tick re-raise every pane above it.
    /// The menu goes on stealing input while invisible under an opaque pane, and the mode's
    /// failure messages - the clipboard is busy, the screen could not be read - are shown for
    /// one frame and then covered for the rest of their dwell. The All-In-One selector solves
    /// the same problem for its toolbar the same way.
    /// </summary>
    private List<IntPtr> SurfaceHandles()
    {
        var handles = new List<IntPtr>(_panes.Count + 3);
        if (IsHandleCreated)
            handles.Add(Handle);
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed && pane.IsHandleCreated)
                handles.Add(pane.Handle);
        }
        if (ToastWindow.LiveHandle is IntPtr toast && toast != IntPtr.Zero)
            handles.Add(toast);
        if (MeasureMenuHandle is IntPtr menu && menu != IntPtr.Zero)
            handles.Add(menu);
        return handles;
    }

    /// <summary>Render seam: the set KeepOnTop is asked about, so a test can prove the
    /// mode's own toast and menu are not in the "cover me" set.</summary>
    internal IReadOnlyList<IntPtr> SurfaceHandlesForRender() => SurfaceHandles();

    private bool IsPaneHandle(IntPtr handle)
    {
        foreach (var pane in _panes)
        {
            if (!pane.IsDisposed && pane.Handle == handle)
                return true;
        }
        return false;
    }

    /// <param name="window">
    /// The window the confirmation picked, when it was a window rather than a dragged region.
    /// Its handle is what the corner trim needs, and this is the last place that knows it.
    /// </param>
    private void Confirm(SD.Rectangle virtualRect, IntPtr window = default)
    {
        SD.Rectangle requested = virtualRect;
        virtualRect.Intersect(new SD.Rectangle(0, 0, _vs.Width, _vs.Height));
        if (virtualRect.Width < 1 || virtualRect.Height < 1) return;

        SelectedRegionPx = virtualRect;
        BareBackgroundRequested = (WF.Control.ModifierKeys & WF.Keys.Shift) == WF.Keys.Shift;
        // Crop the result from the frozen snapshot so the capture is exactly what was selected.
        _capturedRegion?.Dispose();
        _capturedRegion = CropFrozen(virtualRect);
        TrimWindowShadow(window, wholeWindowCaptured: virtualRect == requested);
        if (_settings is not null)
            _settings.Current.LastCaptureRegion = PreviousRegion.Format(
                new SD.Rectangle(virtualRect.X + _vs.X, virtualRect.Y + _vs.Y, virtualRect.Width, virtualRect.Height));

        Complete(WF.DialogResult.OK);
    }

    private void Complete(WF.DialogResult result)
    {
        DialogResult = result;
        StopAspectSnap();
        StopFollowMotion();
        // A tween outliving the session holds the process-wide 1 ms clock.
        BeginHoverWash(arriving: false, wasHovering: false);
        _handleTween?.Dispose();
        _handleTween = null;
        _handleScale = 1d;
        _hintTween?.Dispose();
        _hintTween = null;
        _hintHold?.Dispose();
        _hintHold = null;
        _hintOpacity = 0d;
        _hintIsMessage = false;
        _barTween?.Dispose();
        _barTween = null;
        _pillTween?.Dispose();
        _pillTween = null;
        _pillHover = false;
        _pillSwap = 0d;
        _pendingGlideTween?.Dispose();
        _pendingGlideTween = null;
        _pendingGlide.Stop();
        _edgeTween?.Dispose();
        _edgeTween = null;
        _edgeGlow = 0d;
        _hoverEdge = MarqueeSide.None;
        _pressedEdge = MarqueeSide.None;
        _sessionEnded = true;
        // The hint counter reaches disk when the session that spent one ENDS, not at app
        // exit. WinShot is a tray app that usually dies with the Windows session rather than
        // leaving through the tray — and killing it is the documented pre-install step — so
        // App.OnExit's save routinely never ran and the five hint sessions restarted after
        // every reboot, which is the one counter whose whole job is to stop. Saved here
        // rather than at open so the Changed handlers (hotkey re-registration) fire with no
        // selector on screen, and fire-and-forget so nothing waits on a disk write.
        if (_hintSessionUnsaved && _settings is not null)
        {
            _hintSessionUnsaved = false;
            _ = _settings.SaveAsync();
        }
        // The session is over: drop the live run and the held canvas with it, so a pooled
        // re-show can never paint a stale measurement over a fresh screen.
        ResetMeasure();
        Capture = false;
        DisposeFrozen(); // free the full snapshot now; _capturedRegion stays for the caller
        Park();
        _completion?.TrySetResult(result);
        _completion = null;
    }

    // ----------------------------------------------------------- handle math

    // The handle geometry itself lives in SelectionHandles, shared with the all-in-one
    // selector; these wrap it with this selector's clamp bounds.
    private SD.Rectangle ResizeRect(SD.Rectangle start, int handle, SD.Point cursor) =>
        LockedRatio is double locked
            ? LockedResize(start, handle, cursor, locked)
            : SelectionHandles.Resize(start, handle, cursor, _vs);

    private SD.Rectangle MoveRect(SD.Rectangle start, int dx, int dy) =>
        SelectionHandles.Move(start, dx, dy, _vs);

    /// <summary>
    /// The ratio the HUD's aspect cell has locked, or null in Freeform (and always null
    /// outside the recording flow, which has no aspect cell). Every path that reshapes the
    /// pending rectangle reads this, so the chip can never describe a rectangle that is no
    /// longer that shape.
    /// </summary>
    private double? LockedRatio =>
        _recordingHud ? RecordingAspectPresets.All[_aspectIndex].Ratio : null;

    /// <summary>
    /// The marquee for the drag in flight, aspect-locked when the HUD's chip says so and
    /// otherwise square while Shift is held. Shared by the live preview and the mouse-up that
    /// commits it so the two can never disagree. The ratio math lives in AllInOneDragLayout
    /// and is tested there — a square is just the 1:1 case of it. Edge snapping still runs
    /// first; where the two disagree the ratio wins, because that is what was asked for.
    /// </summary>
    private SD.Rectangle DragRect()
    {
        SD.Point current = SnappedCursor();
        double? ratio = LockedRatio
            ?? ((WF.Control.ModifierKeys & WF.Keys.Shift) == WF.Keys.Shift ? 1d : null);
        return ratio is double locked
            ? AllInOneDragLayout.CreatePixelRectangle(_dragStartScreen, current, locked)
            : Normalize(_dragStartScreen, current);
    }

    /// <summary>
    /// Resize with the aspect lock on: the corner opposite the handle stays put and the ratio
    /// supplies whichever side the handle does not drive, through the same helper the
    /// All-In-One selector already uses for its ratio drags. An edge handle drives one axis
    /// only — its other coordinate is pinned to the anchor so the ratio derives the rest.
    /// </summary>
    private SD.Rectangle LockedResize(SD.Rectangle start, int handle, SD.Point cursor, double ratio)
    {
        bool movesLeft = handle is 0 or 6 or 7;
        bool movesRight = handle is 2 or 3 or 4;
        bool movesTop = handle is 0 or 1 or 2;
        bool movesBottom = handle is 4 or 5 or 6;

        var anchor = new SD.Point(
            movesLeft ? start.Right : start.Left,
            movesTop ? start.Bottom : start.Top);
        var current = new SD.Point(
            movesLeft || movesRight ? Math.Clamp(cursor.X, _vs.Left, _vs.Right) : anchor.X,
            movesTop || movesBottom ? Math.Clamp(cursor.Y, _vs.Top, _vs.Bottom) : anchor.Y);

        SD.Rectangle rect = AllInOneDragLayout.CreatePixelRectangle(anchor, current, ratio);
        if (_vs.Contains(rect))
            return rect;

        // Clamping the cursor is not enough — the ratio-DERIVED side can still run off the
        // desktop, and it is not the side being dragged, so nothing about the cursor bounds
        // it. Refitting about the rectangle's own centre (what RecordingAspectPresets.Apply
        // does, which is right for picking a ratio off the chip) would slide the anchored
        // corner sideways in one frame. Shrink about the anchor instead: the corner opposite
        // the handle stays exactly where it was and only the size gives.
        bool growsLeft = current.X < anchor.X;
        bool growsUp = current.Y < anchor.Y;
        int roomX = Math.Max(0, growsLeft ? anchor.X - _vs.Left : _vs.Right - anchor.X);
        int roomY = Math.Max(0, growsUp ? anchor.Y - _vs.Top : _vs.Bottom - anchor.Y);
        int width = (int)Math.Floor(Math.Min(Math.Min(rect.Width, roomX), roomY * ratio));
        int height = (int)Math.Floor(width / ratio);
        return new SD.Rectangle(
            growsLeft ? anchor.X - width : anchor.X,
            growsUp ? anchor.Y - height : anchor.Y,
            Math.Max(0, width),
            Math.Max(0, height));
    }

    /// <summary>The smallest selection a handle drag is allowed to leave behind — small enough
    /// that it never fights a deliberate tiny crop, large enough to survive the even-rounding
    /// H.264 needs and to still show its own handles.</summary>
    internal const int MinPendingPx = 8;

    /// <summary>Grows a collapsed selection back to <see cref="MinPendingPx"/> and keeps it on
    /// the virtual desktop.</summary>
    internal static SD.Rectangle EnsureUsable(SD.Rectangle rect, SD.Rectangle bounds)
    {
        int w = Math.Min(Math.Max(rect.Width, MinPendingPx), Math.Max(1, bounds.Width));
        int h = Math.Min(Math.Max(rect.Height, MinPendingPx), Math.Max(1, bounds.Height));
        int x = Math.Clamp(rect.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - w));
        int y = Math.Clamp(rect.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - h));
        return new SD.Rectangle(x, y, w, h);
    }

    private SD.Rectangle EnsureUsablePending(SD.Rectangle rect) => EnsureUsable(rect, _vs);


    internal WF.Cursor CursorForCurrent()
    {
        if (_mode == SelectorMode.Measure)
            return MeasureCursor();

        if (_mode == SelectorMode.Window || _pillHover)
        {
            // Sized for the monitor the pointer is actually on: a custom HCURSOR is drawn at
            // its native pixel size, so a cursor built for the primary reads wrong the moment
            // it crosses onto a monitor at another DPI.
            return SelectorCursors.Camera(
                WinShot.Recording.RecordingMonitorDpi.ScaleFor(MonitorBoundsAt(_currentScreen)));
        }

        if (_pendingScreen is SD.Rectangle p)
        {
            if (_recordingHud)
            {
                // The HUD replaced the Cancel/Done bar, so the bar's rect is a phantom that
                // must not turn the pointer into a Hand over empty dimmed desktop. The HUD's
                // own rows, toggles and aspect chip are what read as buttons now.
                RecordingHudCell? cell = HudGeometry(p).HitTest(HudPoint(_currentScreen, p));
                if (cell is not null and not RecordingHudCell.Dimensions)
                    return WF.Cursors.Hand;
            }
            else
            {
                // The Cancel/Done bar reads as buttons, not as selection ground.
                var (bar, _, _, _) = ActionBarRects(BarAnchor ?? p);
                if (bar.Contains(_currentScreen))
                    return WF.Cursors.Hand;
            }

            int h = _resizeHandle >= 0 ? _resizeHandle : SelectionHandles.HitTest(_currentScreen, p, ScaleAt(_currentScreen));
            if (SelectionHandles.CursorFor(h) is WF.Cursor sizing)
                return sizing;
            // The edge band is inside the move body, so SizeAll used to promise a move over
            // pixels that answer the click-to-snap gesture. The cursor names the gesture.
            if (!_movingPending && _hoverEdge != MarqueeSide.None)
            {
                return _hoverEdge is MarqueeSide.Left or MarqueeSide.Right
                    ? WF.Cursors.SizeWE
                    : WF.Cursors.SizeNS;
            }
            if (_movingPending || p.Contains(_currentScreen))
                return WF.Cursors.SizeAll;
        }
        return WF.Cursors.Cross;
    }

    private const int BarPad = 8;   // placement inset only; button geometry scales in ActionBarRects
    private const int BarGap = 12;

    /// <summary>What the padding cell says. Its keyboard equivalent is [ and ]. Spelled out
    /// rather than abbreviated: it sits between "Cancel" and "Done" on an owner-drawn overlay
    /// that can carry no tooltip, so the label is the only explanation it gets.</summary>
    private const string PadButtonLabel = "Pad edges";

    /// <summary>0..1 hover and press amounts for the bar's three cells (pad, cancel, done),
    /// eased over <see cref="Motion.QuickMs"/> so no fill on the bar snaps.</summary>
    private readonly double[] _barHover = new double[3];
    private readonly double[] _barPress = new double[3];
    private IDisposable? _barTween;

    /// <summary>
    /// Moves the bar's hover/press amounts toward the current state. Indexes match
    /// <see cref="ActionBarRects"/>: 0 = cancel, 1 = done, 2 = pad — the same numbers
    /// <see cref="_hoverBarButton"/> and <see cref="_pressedBarButton"/> already carry.
    /// </summary>
    private void AnimateBarFills()
    {
        double[] fromHover = (double[])_barHover.Clone();
        double[] fromPress = (double[])_barPress.Clone();
        int hoverTarget = _hoverBarButton;
        int pressTarget = _pressedBarButton;

        _barTween?.Dispose();
        _barTween = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            WinShot.Core.Motion.EaseOutCubic,
            progress =>
            {
                for (int i = 0; i < 3; i++)
                {
                    _barHover[i] = fromHover[i] + (((hoverTarget == i ? 1d : 0d) - fromHover[i]) * progress);
                    _barPress[i] = fromPress[i] + (((pressTarget == i ? 1d : 0d) - fromPress[i]) * progress);
                }
                if (BarAnchor is SD.Rectangle p)
                    InvalidateScreenRect(ActionBarRects(p).bar);
            },
            () => _barTween = null);
    }

    /// <summary>
    /// Screen rects for the Cancel/Done bar and its two buttons, given the pending selection.
    /// Centered on the region and placed BELOW it; flipped ABOVE when the region runs to the
    /// screen bottom; tucked INSIDE the region's bottom when it spans the full screen height.
    /// Pure function of the region (+ its monitor) so paint and hit-test always agree.
    /// </summary>
    /// <summary>Pixel-unit UI font sized for the monitor scale, so measure and draw agree
    /// regardless of which pane's DPI renders it (12px = the modal buttons' 9pt at 96).</summary>
    private static SD.Font ActionBarFont(double s) =>
        ThemePalette.UiFontPx(ThemePalette.PxForPt(ThemePalette.TextBodyPt, s));

    private static (SD.Rectangle bar, SD.Rectangle pad, SD.Rectangle cancel, SD.Rectangle done) ActionBarRects(SD.Rectangle r)
    {
        // Same proportions as the record modal's Cancel/Start pair, scaled to the
        // region's monitor: quiet regular-weight labels in roomy rounded buttons.
        SD.Rectangle m = WF.Screen.FromRectangle(r).Bounds;
        double s = RecordingMonitorDpi.ScaleFor(m);
        using var font = ActionBarFont(s);
        int hpad = (int)Math.Round(16 * s);
        int pad = (int)Math.Round(6 * s);
        int gap = (int)Math.Round(8 * s);
        int btnH = (int)Math.Round(30 * s);
        int cancelW = WF.TextRenderer.MeasureText("Cancel", font).Width + hpad * 2;
        int doneW = Math.Max(cancelW, WF.TextRenderer.MeasureText("Done", font).Width + hpad * 2);
        // A word, not a glyph. This cell was the only icon-only control on the bar, the
        // overlay is owner-drawn so it can carry no tooltip, and its icon-font glyph was the
        // only Point-unit font here — Points already resolve against the monitor DPI, so
        // multiplying by the same scale as its pixel-unit neighbours sized it twice and it
        // grew against "Cancel" on every scaled display. A label in ActionBarFont says what
        // the button does and tracks `s` exactly like the two buttons beside it.
        int padW = WF.TextRenderer.MeasureText(PadButtonLabel, font).Width + hpad * 2;
        int barW = pad + padW + gap + cancelW + gap + doneW + pad;
        int barH = pad + btnH + pad;

        SD.Point origin = PlaceActionBar(r, m, barW, barH);
        var bar = new SD.Rectangle(origin.X, origin.Y, barW, barH);
        var padButton = new SD.Rectangle(origin.X + pad, origin.Y + pad, padW, btnH);
        var cancel = new SD.Rectangle(padButton.Right + gap, origin.Y + pad, cancelW, btnH);
        var done = new SD.Rectangle(cancel.Right + gap, origin.Y + pad, doneW, btnH);
        return (bar, padButton, cancel, done);
    }

    /// <summary>
    /// Top-left of the action bar for region <paramref name="r"/> within monitor <paramref name="m"/>:
    /// centered horizontally and placed below the region; flipped above when it would run off the
    /// monitor bottom; tucked inside the region's bottom when the region spans the full height
    /// (no room either side). Clamped to stay on-monitor. Pure — unit-testable without a screen.
    /// </summary>
    internal static SD.Point PlaceActionBar(SD.Rectangle r, SD.Rectangle m, int barW, int barH)
    {
        int x = r.Left + (r.Width - barW) / 2;
        int below = r.Bottom + BarGap;
        int above = r.Top - barH - BarGap;
        int y;
        if (below + barH <= m.Bottom - BarPad) y = below;            // room below the region
        else if (above >= m.Top + BarPad) y = above;                 // else above it
        else y = r.Bottom - barH - BarGap;                           // full-height region: inside the bottom
        x = Math.Clamp(x, m.Left + BarPad, Math.Max(m.Left + BarPad, m.Right - barW - BarPad));
        y = Math.Clamp(y, m.Top + BarPad, Math.Max(m.Top + BarPad, m.Bottom - barH - BarPad));
        return new SD.Point(x, y);
    }

    private void DrawActionBar(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle bar, SD.Rectangle padButton, SD.Rectangle cancel, SD.Rectangle done)
    {
        SD.Rectangle lb = ToLocal(bar, monitorBounds), lp = ToLocal(padButton, monitorBounds),
            lc = ToLocal(cancel, monitorBounds), ld = ToLocal(done, monitorBounds);
        double s = WinShot.Recording.RecordingMonitorDpi.ScaleFor(WF.Screen.FromRectangle(bar).Bounds);
        using var font = ActionBarFont(s);

        // Every cell's fill comes from one place: StateFill over a palette token, moved by a
        // 0..1 hover/press amount that eases over QuickMs. Neutral fills lift on hover and
        // the saturated one dips — a lighter blue under the white "Done" label reads worse
        // than the button at rest — and StateFill is the single place that decides which.
        // The pad cell used to mix two greys of its own (#3A3A3C idle, #303032 pressed) that
        // exist nowhere in ThemePalette, so one bar carried a fourth and fifth neutral shade.
        SD.Color cancelFill = DarkControlPalette.StateFill(ThemePalette.SurfaceAlt, _barHover[0], _barPress[0]);
        // White on the bare accent is 3.65:1, under the 4.5:1 floor; #0060DF clears it at
        // 5.62:1. Same call the all-in-one's SelectedFill made — one decision, both selectors.
        SD.Color doneFill = DarkControlPalette.StateFill(ThemePalette.AccentPressed, _barHover[1], _barPress[1]);
        SD.Color padFill = DarkControlPalette.StateFill(ThemePalette.SurfaceAlt, _barHover[2], _barPress[2]);

        var prev = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        int barRadius = (int)Math.Round(10 * s);
        int btnRadius = (int)Math.Round(6 * s);
        using (var path = GdiPaths.RoundedRect(lb, barRadius))
        {
            using (var bg = new SD.SolidBrush(SD.Color.FromArgb(245, ThemePalette.WindowBg)))
                g.FillPath(bg, path);
            using (var edge = new SD.Pen(ThemePalette.BorderStrong, 1))
                g.DrawPath(edge, path);
        }
        using (var pp = GdiPaths.RoundedRect(lp, btnRadius))
        using (var pb = new SD.SolidBrush(padFill))
            g.FillPath(pb, pp);
        using (var cp = GdiPaths.RoundedRect(lc, btnRadius))
        using (var cb = new SD.SolidBrush(cancelFill))
            g.FillPath(cb, cp);
        using (var dp = GdiPaths.RoundedRect(ld, btnRadius))
        using (var db = new SD.SolidBrush(doneFill))
            g.FillPath(db, dp);
        g.SmoothingMode = prev;

        const WF.TextFormatFlags center = WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(g, PadButtonLabel, font, lp, ThemePalette.TextPrimary, center);
        WF.TextRenderer.DrawText(g, "Cancel", font, lc, ThemePalette.TextPrimary, center);
        WF.TextRenderer.DrawText(g, "Done", font, ld, SD.Color.White, center);
    }

    // ----------------------------------------------------------- recording HUD

    /// <summary>The five toggle cells, in the order the settings card lays them out.</summary>
    private static readonly RecordingHudCell[] HudToggleCells =
    [
        RecordingHudCell.Microphone,
        RecordingHudCell.SystemAudio,
        RecordingHudCell.Webcam,
        RecordingHudCell.Clicks,
        RecordingHudCell.Keystrokes,
    ];

    private static SD.Rectangle HudMonitor(SD.Rectangle region) => WF.Screen.FromRectangle(region).Bounds;

    private static RecordingHudGeometry HudGeometry(SD.Rectangle region)
    {
        SD.Rectangle monitor = HudMonitor(region);
        return RecordingHudLayout.Build(region, monitor, RecordingMonitorDpi.ScaleFor(monitor));
    }

    private static (SD.Rectangle Card, IReadOnlyList<SD.Rectangle> Rows) AspectMenuRects(SD.Rectangle region)
    {
        SD.Rectangle monitor = HudMonitor(region);
        return RecordingHudLayout.AspectMenu(
            HudGeometry(region).CellBounds(RecordingHudCell.Aspect),
            monitor,
            RecordingMonitorDpi.ScaleFor(monitor),
            RecordingAspectPresets.All.Count);
    }

    // ---- HUD motion ---------------------------------------------------------

    private void ResetHudMotion()
    {
        DisposeHudMotion();
        _hudAmount = 0;
        _hudShown = false;
        _menuAmount = 0;
        _hudLastRect = SD.Rectangle.Empty;
        _hudLastRegion = null;
        _hudHoverWash = new HoverWash<RecordingHudCell>(InvalidateHud);
        _hudPressWash = new HoverWash<RecordingHudCell>(InvalidateHud);
        _menuHoverWash = new HoverWash<int>(InvalidateHud);
        _aspectChipAmount = new StateAmount(InvalidateHud);
        _aspectChipAmount.Reset(_aspectIndex == RecordingAspectPresets.FreeformIndex ? 0 : 1);
        _toggleAmounts = new StateAmount[HudToggleCells.Length];
        for (int i = 0; i < _toggleAmounts.Length; i++)
        {
            _toggleAmounts[i] = new StateAmount(InvalidateHud);
            _toggleAmounts[i].Reset(_recordingToggles[HudToggleCells[i]] ? 1 : 0);
        }
    }

    private void DisposeHudMotion()
    {
        // The aspect glide is HUD motion too: a selector disposed with the ratio snap in flight
        // otherwise left its Motion.Run handle alive forever, pinning timeBeginPeriod(1) for the
        // life of the process.
        StopAspectSnap();
        _hudMotion?.Dispose();
        _hudMotion = null;
        _menuMotion?.Dispose();
        _menuMotion = null;
        _hudHoverWash?.Dispose();
        _hudHoverWash = null;
        _hudPressWash?.Dispose();
        _hudPressWash = null;
        _menuHoverWash?.Dispose();
        _menuHoverWash = null;
        _aspectChipAmount?.Dispose();
        _aspectChipAmount = null;
        if (_toggleAmounts is not null)
        {
            foreach (StateAmount amount in _toggleAmounts)
                amount.Dispose();
            _toggleAmounts = null;
        }
    }

    /// <summary>
    /// Slack around the HUD rect: the entrance rise, the cards' antialiased edge, and the two
    /// things drawn just outside the block (the hover-label pill above it and the "Esc to
    /// cancel" line below). Generous enough to cover all of that at 200%.
    /// </summary>
    private const int HudRiseSlackPx = 48;

    /// <summary>
    /// Repaints only what the HUD covers — both cards, the popover, and the slack the
    /// entrance rise needs — on whichever surfaces they land on. Every cross-fade frame goes
    /// through here rather than asking a 4K monitor to repaint whole.
    /// </summary>
    private void InvalidateHud()
    {
        SD.Rectangle rect;
        if (_pendingScreen is SD.Rectangle region)
        {
            rect = HudGeometry(region).Bounds;
            if (_aspectMenuOpen || _menuAmount > 0)
                rect = SD.Rectangle.Union(rect, AspectMenuRects(region).Card);
            _hudLastRect = rect;
            _hudLastRegion = region;
        }
        else if (!_hudLastRect.IsEmpty)
        {
            rect = _hudLastRect; // the exit tween still has to paint where the HUD used to be
        }
        else
        {
            InvalidateAllSurfaces();
            return;
        }

        InvalidateScreenRect(SD.Rectangle.Inflate(rect, HudRiseSlackPx, HudRiseSlackPx));
    }

    /// <summary>
    /// The cards arrive and leave as one block: opacity plus an 8 px rise over
    /// <c>Motion.EnterDurationMs</c>, eased out on the way in and in on the way out so the
    /// exit reads as the entrance played backwards.
    /// </summary>
    private void ShowHud(bool visible)
    {
        if (!_recordingHud || _hudShown == visible)
            return;

        _hudShown = visible;
        if (visible)
            _hudLastRegion = _pendingScreen;
        double from = _hudAmount;
        double to = visible ? 1d : 0d;
        _hudMotion?.Dispose();
        _hudMotion = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.EnterDurationMs,
            visible ? WinShot.Core.Motion.EaseOutCubic : WinShot.Core.Motion.EaseInCubic,
            amount =>
            {
                _hudAmount = from + ((to - from) * amount);
                InvalidateHud();
            },
            () => _hudMotion = null);
    }

    /// <summary>The popover scales up from 0.94 and fades in, and plays that backwards on
    /// dismissal — so the twelve rows never cut in or out.</summary>
    private void SetAspectMenuOpen(bool open)
    {
        if (_aspectMenuOpen == open)
            return;

        _aspectMenuOpen = open;
        _aspectHover = -1;
        _menuHoverWash?.To(null);

        double from = _menuAmount;
        double to = open ? 1d : 0d;
        _menuMotion?.Dispose();
        _menuMotion = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.QuickMs,
            open ? WinShot.Core.Motion.EaseOutCubic : WinShot.Core.Motion.EaseInCubic,
            amount =>
            {
                _menuAmount = from + ((to - from) * amount);
                InvalidateHud();
            },
            () => _menuMotion = null);
    }

    private double HoverAmount(RecordingHudCell cell) => _hudHoverWash?[cell] ?? 0d;

    private double PressAmount(RecordingHudCell cell) => _hudPressWash?[cell] ?? 0d;

    private double ToggleAmount(RecordingHudCell cell)
    {
        int index = Array.IndexOf(HudToggleCells, cell);
        if (_toggleAmounts is null || index < 0)
            return _recordingToggles[cell] ? 1d : 0d;
        return _toggleAmounts[index].Value;
    }

    // ---- HUD input ----------------------------------------------------------

    /// <summary>Returns true when the HUD swallowed the press.</summary>
    private bool HandleHudMouseDown(SD.Rectangle region, SD.Point screen)
    {
        // Mid-entrance the whole block is blitted up to 8 px BELOW where its geometry says it
        // is, so the raw screen point hit-tests the cell above the one drawn under the
        // pointer — a press aimed at "Record video" started a GIF at 150%.
        screen = HudPoint(screen, region);

        if (_aspectMenuOpen)
        {
            var (card, rows) = AspectMenuRects(region);
            SD.Point inMenu = AspectMenuPoint(screen, card);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Contains(inMenu))
                {
                    PickAspect(i, region);
                    return true;
                }
            }
            // A click anywhere else closes the menu and is not passed through, so dismissing
            // it can never also start a new marquee.
            SetAspectMenuOpen(false);
            return true;
        }

        RecordingHudGeometry hud = HudGeometry(region);
        RecordingHudCell? hit = hud.HitTest(screen);
        if (hit is null)
        {
            // A press on the cards' own padding — the 8 dip around the record rows, the gap
            // between the two cards — must be inert, and so must the pills the HUD paints
            // outside them. Falling through threw the framed selection away and started a new
            // marquee under the chrome the user was aiming at.
            return HudChromeContains(hud, region, screen);
        }
        if (hit == RecordingHudCell.Dimensions)
            return true; // the readout is inert but still part of the card

        _hudPressed = hit;
        _hudPressWash?.To(hit);
        _hudFocus = Array.IndexOf(HudFocusOrder, hit.Value);
        InvalidateHud();
        return true;
    }

    private void HandleHudClick(RecordingHudCell cell)
    {
        if (_pendingScreen is not SD.Rectangle)
            return;

        switch (cell)
        {
            case RecordingHudCell.Aspect:
                SetAspectMenuOpen(!_aspectMenuOpen);
                return;
            case RecordingHudCell.RecordGif:
            case RecordingHudCell.RecordVideo:
                ConfirmRecording(cell == RecordingHudCell.RecordGif);
                return;
            case RecordingHudCell.Dimensions:
                return;
            default:
                _recordingToggles = _recordingToggles.Toggled(cell);
                int index = Array.IndexOf(HudToggleCells, cell);
                if (_toggleAmounts is not null && index >= 0)
                    _toggleAmounts[index].To(_recordingToggles[cell] ? 1 : 0);
                InvalidateHud();
                return;
        }
    }

    /// <summary>
    /// Tab / Shift+Tab and Left / Right walk the cells; Space works the focused one. Without
    /// this the toggles and the aspect picker were mouse-only, so a keyboard user could not
    /// turn the microphone on at all.
    /// </summary>
    private bool HandleHudKeyDown(WF.KeyEventArgs e)
    {
        if (_aspectMenuOpen)
        {
            // The popover owns the screen while it is up, so it swallows every key it does not
            // act on rather than letting it reach the surface behind it. Falling through used
            // to hand Enter to the generic confirm branch, which started the take with the
            // twelve rows still painted on top.
            switch (e.KeyCode)
            {
                case WF.Keys.Escape:
                    SetAspectMenuOpen(false);
                    break;
                case WF.Keys.Up:
                case WF.Keys.Down:
                {
                    int count = RecordingAspectPresets.All.Count;
                    int step = e.KeyCode == WF.Keys.Down ? 1 : -1;
                    int start = _aspectHover < 0 ? _aspectIndex : _aspectHover;
                    _aspectHover = (((start + step) % count) + count) % count;
                    _menuHoverWash?.To(_aspectHover);
                    InvalidateHud();
                    break;
                }
                case WF.Keys.Space:
                case WF.Keys.Enter:
                    // Nothing hovered yet means the highlighted row is the current preset —
                    // the same row Up/Down steps from, so it is the one Enter commits.
                    if (_pendingScreen is SD.Rectangle menuRegion)
                        PickAspect(_aspectHover < 0 ? _aspectIndex : _aspectHover, menuRegion);
                    break;
            }
            e.Handled = e.SuppressKeyPress = true;
            return true;
        }

        switch (e.KeyCode)
        {
            case WF.Keys.Tab:
                MoveHudFocus(e.Shift ? -1 : 1);
                e.Handled = e.SuppressKeyPress = true;
                return true;
            case WF.Keys.Left:
            case WF.Keys.Right:
                if (_hudFocus < 0)
                    return false;
                MoveHudFocus(e.KeyCode == WF.Keys.Right ? 1 : -1);
                e.Handled = e.SuppressKeyPress = true;
                return true;
            case WF.Keys.Space:
            case WF.Keys.Enter:
                // Both keys work the focused cell, so the focus ring never lies about what the
                // primary key does — Enter on the ringed "Record GIF" row used to record video.
                // Alt+Enter stays the global force-GIF shortcut that row advertises, and a
                // plain Enter with nothing focused still falls through to "record video".
                if (_hudFocus < 0 || (e.KeyCode == WF.Keys.Enter && e.Alt))
                    return false;
                HandleHudClick(HudFocusOrder[_hudFocus]);
                e.Handled = e.SuppressKeyPress = true;
                return true;
        }
        return false;
    }

    private void MoveHudFocus(int step)
    {
        int count = HudFocusOrder.Length;
        int next = _hudFocus < 0
            ? (step > 0 ? 0 : count - 1)
            : ((((_hudFocus + step) % count) + count) % count);
        _hudFocus = next;
        AnnounceHudCell(HudFocusOrder[next]);
        InvalidateHud();
    }

    /// <summary>
    /// The HUD is one owner-drawn surface, so a cell cannot be its own accessible child.
    /// Naming the form after whatever cell has focus or hover is what lets a screen reader
    /// say "Microphone" instead of announcing an unnamed graphic.
    /// </summary>
    private void AnnounceHudCell(RecordingHudCell? cell)
    {
        string label = cell is RecordingHudCell value
            ? RecordingHudLayout.LabelFor(value)
            : "Recording options";
        if (string.Equals(AccessibleName, label, StringComparison.Ordinal))
            return;
        AccessibleName = label;
        try { AccessibilityNotifyClients(WF.AccessibleEvents.NameChange, -1); }
        catch (Exception ex) { Log.Error("Could not announce the recording HUD cell", ex); }
    }

    private void UpdateHudHover()
    {
        if (_pendingScreen is not SD.Rectangle region)
        {
            if (_hudHover is null && _aspectHover < 0)
                return;
            _hudHover = null;
            _aspectHover = -1;
            _hudHoverWash?.To(null);
            _menuHoverWash?.To(null);
            InvalidateAllSurfaces();
            return;
        }

        SD.Point pointer = HudPoint(_currentScreen, region);
        if (_aspectMenuOpen)
        {
            var (card, rows) = AspectMenuRects(region);
            SD.Point inMenu = AspectMenuPoint(pointer, card);
            int hover = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Contains(inMenu))
                {
                    hover = i;
                    break;
                }
            }
            if (hover != _aspectHover)
            {
                _aspectHover = hover;
                _menuHoverWash?.To(hover < 0 ? null : hover);
                InvalidateHud();
            }
            return;
        }

        RecordingHudCell? hit = HudGeometry(region).HitTest(pointer);
        if (hit == _hudHover)
            return;
        _hudHover = hit;
        _hudHoverWash?.To(hit);
        AnnounceHudCell(hit ?? (_hudFocus >= 0 ? HudFocusOrder[_hudFocus] : null));
        InvalidateHud();
    }

    /// <summary>
    /// Snaps the pending selection into the picked ratio over a short glide. An instant
    /// correction reads as a glitch; watching the rect travel is what makes the ratio legible.
    /// </summary>
    private void PickAspect(int index, SD.Rectangle region)
    {
        SetAspectMenuOpen(false);
        _aspectIndex = Math.Clamp(index, 0, RecordingAspectPresets.All.Count - 1);
        _aspectChipAmount?.To(_aspectIndex == RecordingAspectPresets.FreeformIndex ? 0 : 1);
        InvalidateAllSurfaces();

        double? ratio = RecordingAspectPresets.All[_aspectIndex].Ratio;
        if (ratio is not double value)
            return;

        // Clamped to the HUD's own monitor, not the whole virtual desktop: growing the region
        // across a monitor seam left the marquee and the controls that drive it on different
        // displays.
        SD.Rectangle target = RecordingAspectPresets.Apply(region, value, HudMonitor(region));
        if (target == region)
            return;

        StopAspectSnap();
        var tween = new RectangleTween();
        tween.Retarget(region, target);
        _aspectTween = tween;
        _aspectTarget = target;
        _aspectMotion = WinShot.Core.Motion.Run(
            WinShot.Core.Motion.GlideDurationMs,
            WinShot.Core.Motion.EaseOutCubic,
            amount =>
            {
                if (_aspectTween is not RectangleTween live)
                    return;
                live.Update((float)amount);
                _pendingScreen = amount >= 1 ? target : live.Current;
                InvalidateAllSurfaces();
            },
            () =>
            {
                _aspectTween = null;
                _aspectMotion = null;
                _aspectTarget = null;
                _pendingScreen = target;
                InvalidateAllSurfaces();
            });
    }

    /// <summary>
    /// Ends the aspect glide, landing on the ratio that was picked. Disposing a Motion.Run
    /// handle is documented to skip its final frame and its onDone, so without this a Record
    /// press a few frames after picking 16:9 recorded whatever half-corrected rectangle the
    /// ease had reached.
    /// </summary>
    private void StopAspectSnap()
    {
        if (_aspectMotion is not null && _aspectTarget is SD.Rectangle target)
            _pendingScreen = target;
        _aspectTarget = null;
        _aspectMotion?.Dispose();
        _aspectMotion = null;
        _aspectTween?.Stop();
        _aspectTween = null;
    }

    private void ConfirmRecording(bool isGif)
    {
        // Land the glide BEFORE reading the rect: the pending selection is what gets recorded.
        StopAspectSnap();
        if (_pendingScreen is not SD.Rectangle region)
            return;

        RecordingChoice = new RecordingHudChoice(isGif, _recordingToggles);
        Confirm(VirtualFromScreen(region));
    }

    // ---- HUD painting -------------------------------------------------------

    private void DrawRecordingHud(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle region)
    {
        double amount = Math.Clamp(_hudAmount, 0d, 1d);
        if (amount <= 0.002)
            return;
        if (amount >= 0.998)
        {
            PaintHud(g, monitorBounds, region);
            return;
        }

        // Mid-entrance (or mid-exit) the whole block is drawn once into an ARGB buffer and
        // blitted faded and risen. GDI's TextRenderer cannot draw translucent text, so fading
        // the cards in place is not an option — the buffer is.
        double s = RecordingMonitorDpi.ScaleFor(HudMonitor(region));
        SD.Rectangle block = HudGeometry(region).Bounds;
        if (_aspectMenuOpen || _menuAmount > 0)
            block = SD.Rectangle.Union(block, AspectMenuRects(region).Card);
        block.Inflate(HudRiseSlackPx, HudRiseSlackPx);
        SD.Rectangle painted = SD.Rectangle.Intersect(block, monitorBounds);
        if (painted.Width <= 0 || painted.Height <= 0)
            return;

        using var buffer = new SD.Bitmap(painted.Width, painted.Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using (SD.Graphics bg = SD.Graphics.FromImage(buffer))
            PaintHud(bg, painted, region);

        var matrix = new SD.Imaging.ColorMatrix { Matrix33 = (float)amount };
        using var attributes = new SD.Imaging.ImageAttributes();
        attributes.SetColorMatrix(matrix);
        SD.Rectangle dest = ToLocal(painted, monitorBounds);
        dest.Offset(0, (int)Math.Round(8 * s * (1 - amount)));
        g.DrawImage(
            buffer, dest, 0, 0, painted.Width, painted.Height, SD.GraphicsUnit.Pixel, attributes);
    }

    private void PaintHud(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle region)
    {
        SD.Rectangle monitor = HudMonitor(region);
        double s = RecordingMonitorDpi.ScaleFor(monitor);
        RecordingHudGeometry hud = HudGeometry(region);
        if (!hud.Bounds.IntersectsWith(monitorBounds) && _menuAmount <= 0)
            return;

        using SD.Font labelFont = HudLabelFont(s);
        // The tick under a lit toggle comes from the icon family the glyph above it does, at
        // the label's size: a Segoe UI "✓" beside a Segoe Fluent glyph put two families and
        // two stroke weights on one 40 px plate.
        using SD.Font checkFont = ThemePalette.IconFontPx(
            Math.Max(9, RecordingMonitorDpi.Px(RecordingHudLayout.LabelFontDip, s)));
        using SD.Font valueFont = ThemePalette.UiFontPx(
            Math.Max(10, RecordingMonitorDpi.Px(RecordingHudLayout.ValueFontDip, s)), SD.FontStyle.Bold);
        using SD.Font glyphFont = ThemePalette.IconFontPx(
            Math.Max(10, RecordingMonitorDpi.Px(RecordingHudLayout.GlyphFontDip, s)));

        DrawHudCard(g, monitorBounds, hud.SettingsCard, s);
        DrawHudCard(g, monitorBounds, hud.ActionCard, s);

        const WF.TextFormatFlags Centre = WF.TextFormatFlags.HorizontalCenter |
                                          WF.TextFormatFlags.VerticalCenter |
                                          WF.TextFormatFlags.SingleLine |
                                          WF.TextFormatFlags.NoPadding;

        foreach ((RecordingHudCell cell, SD.Rectangle bounds) in hud.Cells)
        {
            SD.Rectangle local = ToLocal(bounds, monitorBounds);
            double hover = HoverAmount(cell);
            double press = PressAmount(cell);

            switch (cell)
            {
                case RecordingHudCell.Dimensions:
                    WF.TextRenderer.DrawText(
                        g, $"{region.Width} × {region.Height}", valueFont, local,
                        ThemePalette.TextPrimary, Centre);
                    break;

                case RecordingHudCell.Aspect:
                {
                    double chip = _aspectChipAmount?.Value
                        ?? (_aspectIndex == RecordingAspectPresets.FreeformIndex ? 0d : 1d);
                    DrawHudPlate(g, local, s, Math.Max(hover, _menuAmount), press, chip);
                    WF.TextRenderer.DrawText(
                        g, RecordingAspectPresets.All[_aspectIndex].Label, labelFont, local,
                        DarkControlPalette.Mix(ThemePalette.TextPrimary, SD.Color.White, chip),
                        Centre);
                    break;
                }

                case RecordingHudCell.RecordGif:
                case RecordingHudCell.RecordVideo:
                {
                    bool primary = cell == RecordingHudCell.RecordVideo;
                    DrawHudRow(g, local, s, hover, press, primary);
                    int inset = (int)Math.Round(12 * s);
                    var labelRect = new SD.Rectangle(
                        local.Left + inset, local.Top, local.Width - (inset * 2), local.Height);
                    WF.TextRenderer.DrawText(
                        g, RecordingHudLayout.LabelFor(cell), labelFont, labelRect,
                        primary ? SD.Color.White : ThemePalette.TextPrimary,
                        WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter |
                        WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
                    WF.TextRenderer.DrawText(
                        g, RecordingHudLayout.ShortcutFor(cell), labelFont, labelRect,
                        // Solid white on the accent fill, the same as the label beside it: the
                        // dimmed variant computed 2.91:1, under even the 3:1 non-text floor.
                        primary ? SD.Color.White : ThemePalette.TextSecondary,
                        WF.TextFormatFlags.Right | WF.TextFormatFlags.VerticalCenter |
                        WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
                    break;
                }

                default:
                {
                    double on = ToggleAmount(cell);
                    DrawHudPlate(g, local, s, hover, press, on);
                    var glyphRect = new SD.Rectangle(
                        local.Left, local.Top, local.Width, local.Height - (int)Math.Round(8 * s));
                    WF.TextRenderer.DrawText(
                        g, RecordingHudLayout.GlyphFor(cell), glyphFont, glyphRect,
                        DarkControlPalette.Mix(ThemePalette.TextPrimary, SD.Color.White, on), Centre);
                    // A checkmark under the glyph, so "on" survives a screenshot of the HUD in
                    // greyscale as well as in colour. It fades up out of the card's own fill,
                    // so the toggle cross-fades instead of the tick popping.
                    // Only while it is actually on its way in: the fade is a colour lerp from
                    // WindowBg, which is invisible on the card but a dark ghost check on the
                    // hover and press plates DrawHudPlate puts under the same pixels.
                    if (on > 0.002)
                    {
                        var tickRect = new SD.Rectangle(
                            local.Left, local.Bottom - (int)Math.Round(16 * s), local.Width, (int)Math.Round(12 * s));
                        WF.TextRenderer.DrawText(
                            g, "", checkFont, tickRect,
                            DarkControlPalette.Mix(ThemePalette.WindowBg, SD.Color.White, on), Centre);
                    }
                    break;
                }
            }
        }

        // Full-height hairlines between the settings cells.
        using (var pen = new SD.Pen(ThemePalette.BorderStrong, 1))
        {
            for (int i = 1; i < hud.Cells.Count; i++)
            {
                (RecordingHudCell cell, SD.Rectangle bounds) = hud.Cells[i];
                if (cell is RecordingHudCell.RecordGif or RecordingHudCell.RecordVideo)
                    continue;
                SD.Rectangle local = ToLocal(bounds, monitorBounds);
                int inset = (int)Math.Round(10 * s);
                g.DrawLine(pen, local.Left, local.Top + inset, local.Left, local.Bottom - inset);
            }
        }

        // Keyboard focus ring, so Tab traversal is visible.
        if (_hudFocus >= 0)
        {
            SD.Rectangle focusCell = hud.CellBounds(HudFocusOrder[_hudFocus]);
            if (!focusCell.IsEmpty)
            {
                SD.Rectangle ring = SD.Rectangle.Inflate(
                    ToLocal(focusCell, monitorBounds), -(int)Math.Round(3 * s), -(int)Math.Round(5 * s));
                SD.Drawing2D.SmoothingMode previous = g.SmoothingMode;
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = GdiPaths.RoundedRect(ring, (int)Math.Round(7 * s)))
                using (var pen = new SD.Pen(ThemePalette.AccentRing, Math.Max(1f, (float)(2 * s))))
                    g.DrawPath(pen, path);
                g.SmoothingMode = previous;
            }
        }

        // Escape backs out. The two record rows print their own shortcuts, so this is the one
        // way out the HUD would otherwise never mention. Below the block where the monitor
        // allows, above it when the block is already parked on the bottom edge.
        //
        // It rides in the same pill every other floating string on this surface gets. Bare on
        // the dimmed desktop it was TextSecondary over whatever happened to be behind it —
        // 1.81:1 over a white window, which is a legibility failure no single screenshot shows.
        SD.Rectangle hintLocal = ToLocal(HudHintPill(hud, monitor, s, labelFont), monitorBounds);
        FillPill(g, hintLocal);
        WF.TextRenderer.DrawText(g, HudEscHint, labelFont, hintLocal, ThemePalette.TextSecondary, Centre);

        // What the hovered glyph means. The toggles are icon-only, so without this the
        // microphone and the webcam are two shapes the user has to guess between.
        DrawHudTip(g, monitorBounds, hud, monitor, s, labelFont);

        if (_menuAmount > 0)
            DrawAspectMenu(g, monitorBounds, region, s, labelFont);
    }

    /// <summary>Labels the hovered or focused cell in a small pill above the settings card.</summary>
    private void DrawHudTip(
        SD.Graphics g,
        SD.Rectangle monitorBounds,
        RecordingHudGeometry hud,
        SD.Rectangle monitor,
        double s,
        SD.Font font)
    {
        SD.Rectangle pill = HudTipPill(hud, monitor, s, font, out string label);
        if (pill.IsEmpty)
            return;

        SD.Rectangle local = ToLocal(pill, monitorBounds);
        FillPill(g, local);
        WF.TextRenderer.DrawText(
            g, label, font, local, ThemePalette.TextPrimary,
            WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter |
            WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
    }

    private const string HudEscHint = "Esc to cancel";

    /// <summary>
    /// A screen point in the HUD's own geometry space. The entrance blits the block with a
    /// downward offset that decays to zero, so during those 280 ms paint and geometry disagree
    /// by exactly that offset and the hit-test has to take it back out.
    /// </summary>
    private SD.Point HudPoint(SD.Point screen, SD.Rectangle region)
    {
        double amount = Math.Clamp(_hudAmount, 0d, 1d);
        if (amount >= 0.998)
            return screen;

        double s = RecordingMonitorDpi.ScaleFor(HudMonitor(region));
        return new SD.Point(screen.X, screen.Y - (int)Math.Round(8 * s * (1 - amount)));
    }

    /// <summary>The label face the HUD's floating pills are measured and drawn with. Shared so
    /// the hit-test can reproduce the rects the painter laid out.</summary>
    private static SD.Font HudLabelFont(double s) => ThemePalette.UiFontPx(
        Math.Max(9, RecordingMonitorDpi.Px(RecordingHudLayout.LabelFontDip, s)));

    /// <summary>
    /// The "Esc to cancel" pill. It is painted beside the cards, so it is NOT inside
    /// <see cref="RecordingHudGeometry.Bounds"/> — both the painter and the hit-test read it
    /// from here, or a press on it falls through and starts a fresh marquee.
    /// </summary>
    private static SD.Rectangle HudHintPill(
        RecordingHudGeometry hud, SD.Rectangle monitor, double s, SD.Font font)
    {
        SD.Size text = WF.TextRenderer.MeasureText(HudEscHint, font);
        int height = text.Height + (int)Math.Round(6 * s);
        int width = text.Width + (int)Math.Round(16 * s);
        int top = hud.ActionCard.Bottom + (int)Math.Round(6 * s);
        if (top + height > monitor.Bottom)
            top = hud.SettingsCard.Top - height - (int)Math.Round(6 * s);
        return new SD.Rectangle(
            hud.ActionCard.Left + ((hud.ActionCard.Width - width) / 2), top, width, height);
    }

    /// <summary>
    /// The hover/focus tip pill, or <see cref="SD.Rectangle.Empty"/> when nothing is labelled.
    /// The other half of the chrome the HUD paints outside its own cards.
    /// </summary>
    private SD.Rectangle HudTipPill(
        RecordingHudGeometry hud, SD.Rectangle monitor, double s, SD.Font font, out string label)
    {
        label = "";
        if (_aspectMenuOpen || _menuAmount > 0)
            return SD.Rectangle.Empty;

        RecordingHudCell? cell = _hudHover ?? (_hudFocus >= 0 ? HudFocusOrder[_hudFocus] : null);
        if (cell is not RecordingHudCell hovered ||
            hovered is RecordingHudCell.RecordGif or RecordingHudCell.RecordVideo)
        {
            return SD.Rectangle.Empty;
        }

        label = RecordingHudLayout.LabelFor(hovered);
        SD.Rectangle cellBounds = hud.CellBounds(hovered);
        if (label.Length == 0 || cellBounds.IsEmpty)
        {
            label = "";
            return SD.Rectangle.Empty;
        }

        SD.Size text = WF.TextRenderer.MeasureText(label, font);
        int pad = (int)Math.Round(8 * s);
        int width = text.Width + (pad * 2);
        int height = text.Height + (int)Math.Round(6 * s);
        int top = hud.SettingsCard.Top - height - (int)Math.Round(6 * s);
        if (top < monitor.Top)
            top = hud.ActionCard.Bottom + (int)Math.Round(6 * s); // no room above: sit under the block
        return new SD.Rectangle(
            Math.Clamp(
                cellBounds.Left + ((cellBounds.Width - width) / 2),
                monitor.Left,
                Math.Max(monitor.Left, monitor.Right - width)),
            top,
            width,
            height);
    }

    /// <summary>
    /// True when the point is on the HUD block or on the chrome the HUD paints beside it. Both
    /// pills are drawn in the same ToolbarBg capsule every interactive-looking surface here
    /// gets, so a press on one has to be as inert as a press on the cards' own padding —
    /// falling through threw the framed selection away and started a new marquee.
    /// </summary>
    private bool HudChromeContains(RecordingHudGeometry hud, SD.Rectangle region, SD.Point screen)
    {
        if (hud.Bounds.Contains(screen))
            return true;

        SD.Rectangle monitor = HudMonitor(region);
        double s = RecordingMonitorDpi.ScaleFor(monitor);
        using SD.Font font = HudLabelFont(s);
        return HudHintPill(hud, monitor, s, font).Contains(screen) ||
               HudTipPill(hud, monitor, s, font, out _).Contains(screen);
    }

    /// <summary>The popover's open/close scale at <paramref name="amount"/>. Shared with the
    /// hit-test, which has to undo exactly what the painter applied.</summary>
    private static float AspectMenuScale(double amount) =>
        (float)(0.94 + (0.06 * Math.Clamp(amount, 0d, 1d)));

    private void DrawAspectMenu(
        SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle region, double s, SD.Font font)
    {
        double amount = Math.Clamp(_menuAmount, 0d, 1d);
        if (amount <= 0.002)
            return;
        if (amount >= 0.998)
        {
            PaintAspectMenu(g, monitorBounds, region, s, font);
            return;
        }

        // Scale + fade about the card's own anchor edge, so the popover grows out of the
        // aspect cell rather than cutting in at full size. It is painted into an ARGB buffer
        // and the buffer is what goes through the transform: GDI's TextRenderer ignores
        // Graphics.Transform (so the twelve row labels stayed pinned at full size while the
        // card scaled under them) and drops the alpha channel (so the fade could not be handed
        // to the brushes at all). Blitting one image fixes both.
        SD.Rectangle card = AspectMenuRects(region).Card;
        SD.Rectangle painted = SD.Rectangle.Inflate(card, 2, 2);
        if (painted.Width <= 0 || painted.Height <= 0)
            return;

        using var buffer = new SD.Bitmap(
            painted.Width, painted.Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using (SD.Graphics bg = SD.Graphics.FromImage(buffer))
            PaintAspectMenu(bg, painted, region, s, font);

        using var attributes = new SD.Imaging.ImageAttributes();
        attributes.SetColorMatrix(new SD.Imaging.ColorMatrix { Matrix33 = (float)amount });

        SD.Rectangle anchor = ToLocal(card, monitorBounds);
        float pivotX = anchor.Left + (anchor.Width / 2f);
        float pivotY = anchor.Bottom;
        float scale = AspectMenuScale(amount);
        SD.Drawing2D.Matrix saved = g.Transform;
        g.TranslateTransform(pivotX, pivotY);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-pivotX, -pivotY);
        g.DrawImage(
            buffer, ToLocal(painted, monitorBounds), 0, 0, painted.Width, painted.Height,
            SD.GraphicsUnit.Pixel, attributes);
        g.Transform = saved;
        saved.Dispose();
    }

    private void PaintAspectMenu(
        SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle region, double s, SD.Font font)
    {
        var (card, rows) = AspectMenuRects(region);
        DrawHudCard(g, monitorBounds, card, s);
        for (int i = 0; i < rows.Count; i++)
        {
            SD.Rectangle local = ToLocal(rows[i], monitorBounds);
            double hover = _menuHoverWash?[i] ?? (i == _aspectHover ? 1d : 0d);
            DrawHudPlate(g, local, s, hover, 0, 0);
            int inset = (int)Math.Round(10 * s);
            var textRect = new SD.Rectangle(local.Left + inset, local.Top, local.Width - (inset * 2), local.Height);
            WF.TextRenderer.DrawText(
                g, RecordingAspectPresets.All[i].Label, font, textRect,
                // AccentRing, not Accent: the picked row is text on the card, and the raw
                // accent only clears 4.5:1 against it on a dark desktop.
                i == _aspectIndex ? ThemePalette.AccentRing : ThemePalette.TextPrimary,
                WF.TextFormatFlags.Left | WF.TextFormatFlags.VerticalCenter |
                WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding);
        }
    }

    /// <summary>
    /// A HUD-space point in the aspect popover's own geometry space. The popover is painted
    /// through a 0.94→1.0 scale about the card's bottom centre, so during those 140 ms paint
    /// and geometry disagree and a click on the top row would otherwise select the second one.
    /// </summary>
    private SD.Point AspectMenuPoint(SD.Point hudPoint, SD.Rectangle card)
    {
        double amount = Math.Clamp(_menuAmount, 0d, 1d);
        if (amount >= 0.998)
            return hudPoint;

        double scale = AspectMenuScale(amount);
        double pivotX = card.Left + (card.Width / 2.0);
        double pivotY = card.Bottom;
        return new SD.Point(
            (int)Math.Round(pivotX + ((hudPoint.X - pivotX) / scale)),
            (int)Math.Round(pivotY + ((hudPoint.Y - pivotY) / scale)));
    }

    /// <summary>
    /// The container every floating string on the HUD sits in: an opaque toolbar fill with the
    /// standard hairline. Nothing on this surface may be text alone — what is behind the HUD is
    /// the dimmed desktop, i.e. arbitrary content, so an uncontained string has no contrast
    /// anyone can compute.
    /// </summary>
    private static void FillPill(SD.Graphics g, SD.Rectangle local)
    {
        SD.Drawing2D.SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(local, local.Height / 2))
        {
            using (var fill = new SD.SolidBrush(ThemePalette.ToolbarBg))
                g.FillPath(fill, path);
            using (var edge = new SD.Pen(ThemePalette.BorderStrong, 1))
                g.DrawPath(edge, path);
        }
        g.SmoothingMode = previous;
    }

    private static void DrawHudCard(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle card, double s)
    {
        SD.Rectangle local = ToLocal(card, monitorBounds);
        var previous = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(local, (int)Math.Round(12 * s)))
        {
            // Fully opaque: at 96% the accent-on-card row dropped to ~4.19:1 over a bright
            // desktop, and a translucent card cannot carry GDI text in the entrance buffer.
            using (var fill = new SD.SolidBrush(ThemePalette.WindowBg))
                g.FillPath(fill, path);
            using (var edge = new SD.Pen(ThemePalette.BorderStrong, 1))
                g.DrawPath(edge, path);
        }
        g.SmoothingMode = previous;
    }

    /// <summary>
    /// The wash behind a settings cell: a white veil that rises with hover and deepens on
    /// press, cross-faded with the accent plate that marks a toggle as on.
    /// </summary>
    private static void DrawHudPlate(
        SD.Graphics g, SD.Rectangle cell, double s, double hover, double press, double accent)
    {
        double h = Math.Clamp(hover, 0d, 1d);
        double p = Math.Clamp(press, 0d, 1d);
        double on = Math.Clamp(accent, 0d, 1d);
        int veil = (int)Math.Round((ThemePalette.HoverFill.A * h) + (0x1A * p));
        if (veil <= 0 && on <= 0.002)
            return;

        var previous = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        SD.Rectangle plate = SD.Rectangle.Inflate(cell, -(int)Math.Round(4 * s), -(int)Math.Round(8 * s));
        using (var path = GdiPaths.RoundedRect(plate, (int)Math.Round(7 * s)))
        {
            if (veil > 0)
            {
                using var veilBrush = new SD.SolidBrush(
                    SD.Color.FromArgb(Math.Clamp(veil, 0, 255), 0xFF, 0xFF, 0xFF));
                g.FillPath(veilBrush, path);
            }
            if (on > 0.002)
            {
                SD.Color fill = DarkControlPalette.StateFill(Accent, h, p);
                using var accentBrush = new SD.SolidBrush(
                    SD.Color.FromArgb((int)Math.Round(255 * on), fill));
                g.FillPath(accentBrush, path);
            }
        }
        g.SmoothingMode = previous;
    }

    private static void DrawHudRow(
        SD.Graphics g, SD.Rectangle row, double s, double hover, double press, bool primary)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        SD.Color fill = DarkControlPalette.StateFill(
            primary ? Accent : ThemePalette.SurfaceAlt, hover, press);
        using (var path = GdiPaths.RoundedRect(
            SD.Rectangle.Inflate(row, 0, -(int)Math.Round(2 * s)), (int)Math.Round(7 * s)))
        using (var brush = new SD.SolidBrush(fill))
            g.FillPath(brush, path);
        g.SmoothingMode = previous;
    }

    // ----------------------------------------------------------- coordinate helpers

    private static SD.Rectangle ToLocal(SD.Rectangle screenRect, SD.Rectangle monitorBounds) =>
        new(screenRect.X - monitorBounds.X, screenRect.Y - monitorBounds.Y, screenRect.Width, screenRect.Height);

    private static SD.Point ToLocal(SD.Point screenPoint, SD.Rectangle monitorBounds) =>
        new(screenPoint.X - monitorBounds.X, screenPoint.Y - monitorBounds.Y);

    private SD.Rectangle VirtualFromScreen(SD.Rectangle screenRect)
    {
        var rect = new SD.Rectangle(screenRect.X - _vs.X, screenRect.Y - _vs.Y, screenRect.Width, screenRect.Height);
        rect.Intersect(new SD.Rectangle(0, 0, _vs.Width, _vs.Height));
        return rect;
    }

    private static SD.Rectangle Normalize(SD.Point a, SD.Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new SD.Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>
    /// Test seam: where the pointer is. Tests drive selection gestures through the same
    /// handlers the surfaces call, and those read the REAL cursor — which would make every
    /// drag assertion depend on where the mouse happens to be sitting. Nothing but tests
    /// assigns this, and the hidden-desktop runner is the only place it is safe to.
    /// </summary>
    internal static Func<SD.Point>? CursorProvider;

    /// <summary>True physical cursor position — DPI-independent across monitors.</summary>
    private static SD.Point CursorScreen()
    {
        if (CursorProvider is Func<SD.Point> provider)
            return provider();
        return GetCursorPos(out POINT p) ? new SD.Point(p.X, p.Y) : WF.Cursor.Position;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// A non-primary monitor's overlay surface. Owns no state — it forwards input to and
    /// paints from the coordinator, so the selection is one logical thing spanning monitors.
    /// </summary>

    private IDisposable? _followMotionClock;

    /// <summary>
    /// The follow tick drives both the cursor-follow and the hover-highlight glide, so it runs
    /// under the high-resolution clock — at Windows' default ~15.6 ms tick an 8 ms timer
    /// silently halves its frame rate and coalesces under load.
    /// </summary>
    private void StartFollowMotion()
    {
        _followMotionClock ??= WinShot.Core.Motion.Acquire();
        _followTimer.Start();
    }

    private void StopFollowMotion()
    {
        _followTimer.Stop();
        _followMotionClock?.Dispose();
        _followMotionClock = null;
    }

    private sealed class SelectorPane : WF.Form
    {
        private readonly FastRegionSelectorDialog _owner;
        private readonly SD.Rectangle _bounds;

        public SelectorPane(FastRegionSelectorDialog owner, SD.Rectangle monitorBounds, bool freezeScreen)
        {
            _owner = owner;
            _bounds = monitorBounds;
            SelectorChrome.ConfigureSurface(this);
            SelectorChrome.ConfigurePresentation(this, freezeScreen);
            DoubleBuffered = true;
            SetStyle(PaintStyles, true);
            Bounds = monitorBounds;
            // No Opacity reset here: flipping a translucent Form back to Opacity=1.0
            // creates/recreates the window handle (~150-500 ms measured). The frozen swap
            // goes through CaptureExclusion.SetLayeredAlpha instead.
        }

        protected override bool ShowWithoutActivation => true;

        /// <summary>The physical monitor bounds this pane covers (used for dirty-region invalidation).</summary>
        public SD.Rectangle MonitorBounds => _bounds;

        protected override void OnMouseDown(WF.MouseEventArgs e)
        {
            _owner.HandleMouseDown(e);
            Capture = _owner.CapturingPointer;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(WF.MouseEventArgs e)
        {
            _owner.HandleMouseMove();
            Cursor = _owner.CursorForCurrent();
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(WF.MouseEventArgs e)
        {
            Capture = false;
            _owner.HandleMouseUp(e);
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(WF.MouseEventArgs e)
        {
            _owner.HandleMouseWheel(e);
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(WF.KeyEventArgs e)
        {
            _owner.HandleKeyDown(e);
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(WF.KeyEventArgs e)
        {
            _owner.HandleKeyUp(e);
            base.OnKeyUp(e);
        }

        protected override void OnPaint(WF.PaintEventArgs e)
        {
            // Skip the clear once a frozen slice covers the surface — a redundant
            // full-monitor fill is expensive enough to show up in the freeze swap.
            if (!_owner.HasFullSurfaceBackground)
                e.Graphics.Clear(SD.Color.Black);
            _owner.PaintSurface(e.Graphics, _bounds);
            base.OnPaint(e);
        }
    }
}
