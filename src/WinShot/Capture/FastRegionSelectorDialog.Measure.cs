using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// <see cref="FastRegionSelectorDialog.SelectorMode.Measure"/> — PixelSnap-style on-screen
/// measurement, hosted by the selector rather than by a window of its own so it inherits the
/// frozen snapshot, the per-monitor 1:1 panes, the 8 ms follow timer and the dirty-region
/// discipline verbatim.
///
/// Two rules run through all of it. Colour is the grammar: crimson is a distance the app
/// computed, blue is a guide the user placed, and the two never blur. And functional data
/// never animates — hovering re-measures and redraws instantly, because an easing measurement
/// reads as imprecise. The two animations here — the hint strip's entrance and the pill's
/// hover outline — are both chrome; no number and no hairline ever eases.
/// </summary>
public sealed partial class FastRegionSelectorDialog
{
    private readonly MeasureSession _measure = new();

    /// <summary>The live run under the cursor, in screen px. Null when there is nothing to
    /// read from (no frozen snapshot, or the cursor is off the snapshot).</summary>
    private SD.Rectangle? _measured;

    /// <summary>The screen point <see cref="_measured"/> was read at. The wheel re-reads from
    /// here rather than from the pointer, because by then the pointer may be parked on the
    /// pill — which is, by construction, off the object the pill labels.</summary>
    private SD.Point _measuredAt;

    /// <summary>The pointer is on the live pill, which is the capture target. Set from the
    /// same rect the paint uses, so what lights up is what a click acts on.</summary>
    private bool _pillHot;

    private double _measurePillHover;
    private IDisposable? _measurePillHoverTween;

    /// <summary>The two colours the live run is made of, cached against the run it was
    /// sampled for so a stationary cursor does not re-histogram on every follow tick.</summary>
    private (SD.Color Background, SD.Color Foreground)? _contrast;
    private SD.Rectangle _contrastFor;

    /// <summary>The guide being dragged: which axis, and where it currently sits.</summary>
    private (bool Vertical, int At)? _draggingGuide;

    private IDisposable? _hintStripTween;
    private double _hintAmount;

    /// <summary>Which copy the strip is PAINTING, as opposed to which copy its state implies.
    /// The two part company for the length of one re-entrance: the snapshot landing swaps a
    /// one-line loading sentence for three lines of gestures in a much wider capsule, and the
    /// paint that follows it is synchronous. Reading the live state there would show the new
    /// copy at full opacity before the first tween frame — a pop, then a fade to nothing, then
    /// a rise: shown, hidden, shown. So the strip keeps painting the OLD line while it eases
    /// out, the latch is swapped at the trough where nothing is visible, and the new line
    /// rises. Everything else in the copy (the tolerance number, the contrast word) still
    /// derives live, because those change without an entrance to hide the change under.</summary>
    private bool _hintCopyReady;
    private bool _hintCopyFailed;

    /// <summary>The freeze snapshot was attempted and did not arrive, so it never will this
    /// session. Without this there is no way to tell "not yet" from "never", and a mode whose
    /// only persistent surface says "Preparing the screen snapshot…" forever teaches the user
    /// to keep waiting for something that is not coming.</summary>
    private bool _freezeFailed;

    /// <summary>The gesture keys this mode has claimed and not yet seen released. Windows
    /// repeats a held key ~30 times a second, and every gesture here is a discrete action:
    /// one hold, one guide, one toast, one clear. Answering the repeats stacked pixel-identical
    /// holds that Backspace then removed invisibly, and re-raised the mode's one failure
    /// message often enough that its 280 ms entrance never finished — a strobe instead of a
    /// sentence. So a repeat is swallowed for any key the first press claimed. Shift used to
    /// carry a latch of its own for the same reason; this is that, for all of them.</summary>
    private readonly HashSet<WF.Keys> _measureKeysDown = new();

    /// <summary>One log line per session for a failing pixel read — see <see cref="ReadPixels"/>.</summary>
    private bool _pixelReadLogged;

    /// <summary>
    /// The one live right-click menu. Its lifetime is owned here rather than by its own
    /// Closed handler because WinForms' modal-menu filter is a process-global message hook
    /// that keeps touching a drop-down for several messages after it closes — disposing
    /// inside that window takes the process down with an ObjectDisposedException from the
    /// hook, which is not a failure any caller can catch.
    ///
    /// So: closing is immediate and always safe, and disposing waits until the NEXT menu
    /// opens, by which point the hook is long done with this one. At most one menu object
    /// outlives the selector, and the selector itself is pooled for the life of the process.
    /// </summary>
    private WF.ContextMenuStrip? _measureMenu;

    /// <summary>The open menu's own top-level window, or zero. <see cref="SurfaceHandles"/>
    /// needs it: a drop-down opens under the pointer by definition, so without it the very
    /// next follow tick reads the menu as something covering the overlay and re-raises every
    /// pane above it — leaving the menu modal, still eating input, and invisible.</summary>
    private IntPtr MeasureMenuHandle =>
        _measureMenu is { IsDisposed: false, Visible: true } menu && menu.IsHandleCreated
            ? menu.Handle
            : IntPtr.Zero;

    /// <summary>How close the cursor must come to a guide to grab it, at 100 %. A hit target
    /// is a human-scale quantity, not a pixel-scale one, so it scales with the monitor even
    /// though everything geometric in this mode is deliberately physical px.</summary>
    private const int GuideGrabBasePx = 4;

    private int GuideGrabPx => Math.Max(GuideGrabBasePx, (int)Math.Round(
        GuideGrabBasePx * WinShot.Recording.RecordingMonitorDpi.ScaleFor(MonitorBoundsAt(_currentScreen))));

    private const int HintStripBottomGap = 24;

    internal MeasureSession MeasureState => _measure;

    /// <summary>The live measured run, in screen px — for tests, which cannot see a screen.</summary>
    internal SD.Rectangle? MeasuredScreenRect => _measured;

    internal double HintAmount => _hintAmount;

    /// <summary>Render seams for the two pills, so a test can assert they never overlap —
    /// they are the same ground colour, so an overlap is one blob with two texts in it.</summary>
    internal SD.Rectangle? LivePillForRender => MeasurePillScreenRect();

    /// <summary>Render seam: the open right-click menu, so a test can click a row.</summary>
    internal WF.ContextMenuStrip? MeasureMenuForRender => _measureMenu;

    /// <summary>Render seam: opens the menu at a given screen point. The real path reads the
    /// live cursor, which a test on the hidden desktop cannot place.</summary>
    internal void ShowMeasureMenuForRender(SD.Point at) => ShowMeasureMenu(at);

    /// <summary>Render seam: presses a button at the cursor this harness last placed. The
    /// real path reads the live cursor, and a test may not move the user's pointer.</summary>
    internal void MeasureMouseDownForRender(WF.MouseButtons button) =>
        HandleMeasureMouseDown(new WF.MouseEventArgs(button, 1, 0, 0, 0), _currentScreen);

    internal bool IsDraggingGuideForRender => _draggingGuide is not null;

    internal SD.Rectangle? ContrastPillForRender =>
        _measured is SD.Rectangle run ? ContrastPillScreenRect(MonitorBoundsAt(_currentScreen), run) : null;

    /// <summary>
    /// Render seam. Puts the selector into Measure mode over a supplied snapshot without
    /// opening a window, so the measure chrome can be painted into a bitmap and pixel-scanned
    /// on the hidden test desktop — the plan's rule is that render assertions are scanned, not
    /// eyeballed. Nothing in the app calls this; the real path is <see cref="ShowAsync"/>.
    /// </summary>
    internal void EnterMeasureForRender(
        SD.Bitmap? frozen, SD.Rectangle virtualScreen, SD.Point cursor, bool magnifier = false)
    {
        _mode = SelectorMode.Measure;
        _vs = virtualScreen;
        _monitorBounds = virtualScreen;
        Bounds = virtualScreen;
        _frozen = frozen;
        // Off by default so the loupe does not sit over the chrome most of these scans are
        // reading; the real mode forces it on (SelectorOptions.ForMeasure), and the scans
        // that care about the loupe pass magnifier: true.
        _magnifierOn = magnifier;
        ResetMeasure();
        _hintAmount = 1;
        _lastFollowScreen = cursor;
        MoveMeasureCursorForRender(cursor);
    }

    /// <summary>Render seam: the deferred freeze snapshot landing, exactly as
    /// FreezeWhileShownAsync delivers it.</summary>
    internal void LandFrozenSnapshotForRender(SD.Bitmap frozen)
    {
        _frozen = frozen;
        NoteFrozenSnapshotLanded();
    }

    /// <summary>
    /// The freeze snapshot has arrived. Measure mode's entire input IS that bitmap, it lands
    /// 300-1700 ms after the mode opens, and the pointer may not have moved since the hotkey
    /// fired — the follow tick returns early while the cursor is still. So this is where the
    /// first reading comes from, and without it the mode shows a reticle over a dim and
    /// nothing else until the user jiggles the mouse.
    /// </summary>
    internal void NoteFrozenSnapshotLanded()
    {
        if (_mode != SelectorMode.Measure)
            return;

        HandleMeasureMouseMove();
        // The strip's whole copy and its whole width change in this frame — from one loading
        // line to nine gestures. It is the mode's only teaching surface and it already owns
        // an entrance, so it re-enters rather than cutting: out through the floor, swap the
        // copy where nothing is visible, then back up with the new one.
        RestartHintEntrance(0, LatchHintCopy);
    }

    /// <summary>
    /// The freeze snapshot was attempted and did not arrive. There is exactly one attempt per
    /// session, so this is terminal: the mode has no input and will not get one. Latching it
    /// is what lets the hint strip and every gesture's answer stop saying "not yet" — the
    /// toast that reports the failure dwells out, and a persistent surface that keeps
    /// promising a snapshot is the same bug as a failed history load reading "No captures yet".
    /// </summary>
    internal void NoteFrozenSnapshotFailed()
    {
        _freezeFailed = true;
        if (_mode != SelectorMode.Measure)
            return;

        InvalidateAllSurfaces();
        // Nothing else on screen moves at this moment — the overlay does not flip, because
        // there is no snapshot to flip to — so a bare copy swap would be an unaccompanied
        // snap on an error. The strip re-enters instead, swapping at the trough.
        RestartHintEntrance(0, LatchHintCopy);
    }

    /// <summary>Render seam: moves the measuring cursor and re-reads the pixels under it,
    /// exactly as the follow tick does — from wherever the pointer was, so the move can cross
    /// a monitor boundary the way a real one does.</summary>
    internal void MoveMeasureCursorForRender(SD.Point cursor)
    {
        _currentScreen = cursor;
        HandleMeasureMouseMove();
    }

    private void ResetMeasure()
    {
        CloseMeasureMenu();
        _measure.Reset();
        _measured = null;
        _measuredAt = SD.Point.Empty;
        StopPillHover();
        _pillHot = false;
        _measurePillHover = 0;
        _contrast = null;
        _contrastFor = SD.Rectangle.Empty;
        _draggingGuide = null;
        _measureKeysDown.Clear();
        _freezeFailed = false;
        _pixelReadLogged = false;
        _hintAmount = 0;
        LatchHintCopy();
        StopHintEntrance();
    }

    private byte MeasureToleranceFromSettings()
    {
        int stored = _settings?.Current.MeasureTolerance ?? EdgeSnap.DefaultTolerance;
        return (byte)Math.Clamp(stored, 0, 255);
    }

    private void PersistTolerance()
    {
        if (_settings is not null)
            _settings.Current.MeasureTolerance = _measure.Tolerance;
    }

    // ----------------------------------------------------------- reading the frozen pixels

    /// <summary>
    /// One pass over the frozen bitmap: the run around <paramref name="screen"/> and, when
    /// asked, the two colours it is made of. Everything the mode needs from the pixels comes
    /// through here so the LockBits is taken once per mouse-move and released immediately —
    /// GDI+ will not draw a bitmap whose bits are locked, and PaintSurface blits this one.
    /// </summary>
    private (SD.Rectangle? Run, (SD.Color Background, SD.Color Foreground)? Colors) ReadPixels(
        SD.Point screen, byte tolerance, bool wantColors)
    {
        if (_frozen is null)
            return (null, null);

        try
        {
            using var snap = EdgeSnap.Lock(_frozen);
            var inBitmap = new SD.Point(screen.X - _vs.X, screen.Y - _vs.Y);
            if (inBitmap.X < 0 || inBitmap.Y < 0 || inBitmap.X >= snap.Width || inBitmap.Y >= snap.Height)
                return (null, null);

            SD.Rectangle run = snap.DetectEdges(inBitmap, tolerance);
            run.Offset(_vs.X, _vs.Y);
            // A ray that reaches the frame edge degrades to the frame, and the frame is the
            // whole virtual desktop: clamp to the monitor the cursor is on, because "3840
            // across three monitors" is not a measurement anyone asked for, and the dip
            // conversion below is a per-monitor question anyway.
            run.Intersect(MonitorBoundsAt(screen));
            if (run.Width < 1 || run.Height < 1)
                return (null, null);

            if (!wantColors)
                return (run, null);

            var inBitmapRun = run;
            inBitmapRun.Offset(-_vs.X, -_vs.Y);
            var colors = ColorMath.DominantPair(snap.ColorAt, inBitmapRun);
            return (run, colors);
        }
        catch (Exception ex)
        {
            // A transient GDI+ state error must not take the overlay down mid-session; the
            // next mouse-move tries again. Logged once: this is reached from the 8 ms follow
            // tick, so a bitmap that is broken rather than busy would otherwise write ~125
            // lines a second into winshot.log for the life of the session.
            if (!_pixelReadLogged)
            {
                _pixelReadLogged = true;
                Log.Error("Measurement pixel read failed (non-fatal; logged once)", ex);
            }
            return (null, null);
        }
    }

    /// <summary>
    /// The pixel-detected run under the cursor as a candidate for the Area-mode hover ladder,
    /// or null when it is not worth offering. A hairline and "the entire monitor" are both
    /// answers the wheel should not have to step past.
    /// </summary>
    private SD.Rectangle? PixelRungAt(SD.Point screen)
    {
        if (_frozen is null)
            return null;

        // The tolerance the user tuned, not the shipped default: a gradient or an acrylic
        // surface reads as "the snapping is broken" in the Area ladder for exactly the same
        // reason it does in the measure readout, and they answer to the same knob.
        var (run, _) = ReadPixels(screen, MeasureToleranceFromSettings(), wantColors: false);
        if (run is not SD.Rectangle rect || rect.Width < 4 || rect.Height < 4)
            return null;
        return rect == MonitorBoundsAt(screen) ? null : rect;
    }

    /// <summary>One reading, from <paramref name="screen"/>, into the fields the paint reads.
    /// Both the mouse-move and the tolerance wheel come through here so neither can forget
    /// half of it — and so the wheel can re-read at the point the reading was TAKEN at rather
    /// than at a pointer that has since walked onto the pill.</summary>
    private void ReadInto(SD.Point screen)
    {
        var (run, colors) = ReadPixels(screen, _measure.Tolerance, wantColors: true);
        _measured = run;
        if (run is SD.Rectangle rect)
        {
            _measuredAt = screen;
            _contrast = colors;
            _contrastFor = rect;
        }
        else
        {
            _contrast = null;
        }
    }

    /// <summary>The pill moves for reasons other than the pointer — a new reading, a wheel
    /// notch, the snapshot landing under a stationary cursor — and a hand cursor over ground
    /// that is no longer a pill promises a capture that will not happen.</summary>
    private void RefreshPillHot() =>
        SetPillHot(MeasurePillScreenRect() is SD.Rectangle pill && pill.Contains(_currentScreen));

    // ----------------------------------------------------------- input

    private void HandleMeasureMouseMove()
    {
        // A drag can outlive the guide it is holding: Escape and the menu's "Clear
        // measurements" row both empty the list while the button is still down. The drag
        // branch below returns early, so a drag holding a guide that no longer exists stops
        // the mode measuring entirely until the button comes back up — and a mouse-up
        // swallowed by the drop-down may never arrive. Drop the drag with its guide.
        if (_draggingGuide is (bool goneAxis, int goneAt) && _measure.GuideNear(goneAxis, goneAt, 0) is null)
            _draggingGuide = null;

        if (_draggingGuide is (bool vertical, int at))
        {
            int to = vertical
                ? SnapCoordinate(_currentScreen.X, _snapEdgesX)
                : SnapCoordinate(_currentScreen.Y, _snapEdgesY);
            if (to != at && _measure.MoveGuide(vertical, at, to))
            {
                _draggingGuide = (vertical, to);
                InvalidateAllSurfaces();
            }
            // The snap band holds the guide still across a long travel, and a blocked move
            // holds it still indefinitely — but the reticle and the loupe still ride the
            // cursor, so the follow bands repaint whether or not the guide went anywhere.
            FollowMoved();
            return;
        }

        // The pill captures the object it labels, and a target that re-measures as the
        // pointer approaches can never be reached: every step toward it would re-read the
        // pixels under the pointer and move the pill again. So inside the pill — and inside
        // the short corridor between the pill and the edge of the run it labels — the
        // reading holds still. The corridor is only DimensionPillGap px tall and only as
        // wide as the pill, so entering it is a deliberate move OUT of the object towards
        // its number, never an incidental drift across it.
        if (MeasurePillReach() is SD.Rectangle reach && reach.Contains(_currentScreen))
        {
            // Lit only ON the pill, never in the corridor: the outline and the hand cursor
            // promise a capture, and a click in the corridor still copies.
            RefreshPillHot();
            FollowMoved();
            return;
        }

        SD.Rectangle? before = _measured;
        ReadInto(_currentScreen);

        // The hover outline was earned on a pill that no longer exists: fading it out over a
        // DIFFERENT pill would light up something the pointer was never on. The fade is only
        // honest while the reading it belongs to is still the one on screen.
        if (_measured != before && _measurePillHover > 0)
        {
            StopPillHover();
            _pillHot = false;
            _measurePillHover = 0;
        }
        RefreshPillHot();

        // The crosshair, the pill and the contrast readout all move with the cursor, and the
        // run itself can jump anywhere: repaint the union of where it was and where it is,
        // plus the follow bands, rather than the whole (possibly 4K) monitor.
        FollowMoved();
        if (before is SD.Rectangle old)
            InvalidateMeasureRegion(old);
        if (_measured is SD.Rectangle now && now != before)
            InvalidateMeasureRegion(now);
    }

    /// <summary>Room for the tallest pill this mode draws, plus the contrast readout stacked
    /// above it. Derived rather than guessed: the live cross is three lines ("W × H", the dip
    /// value, the ratio) and the point-sized font grows with the monitor.</summary>
    private static int MeasurePillRoom(double scale)
    {
        double key = scale is > 0d and < 100d ? scale : 1d;
        if (_measurePillRoom is (double had, int room) && had == key)
            return room;
        int computed =
            (SelectorChrome.MeasureLabel("0 × 0\n0 × 0\n0 : 0", key).Height * 2) +
            (int)Math.Round(32 * key);
        _measurePillRoom = (key, computed);
        return computed;
    }

    /// <summary>Measured once PER MONITOR SCALE: the font is a point size, so within one
    /// run it can only change when the chrome lands on a display at another DPI.</summary>
    private static (double Scale, int Room)? _measurePillRoom;

    /// <summary>The ground one measurement covers: its own rect plus room for the pill and
    /// the contrast readout, either of which can sit above the line OR flip below it.</summary>
    private void InvalidateMeasureRegion(SD.Rectangle screenRect)
    {
        // Both directions: the pill is centred on the run and a three-line pill is wider than
        // a narrow run, so an 8 px horizontal margin would smear its ends.
        var region = screenRect;
        int room = MeasurePillRoom(ScaleAt(new SD.Point(
            screenRect.Left + (screenRect.Width / 2), screenRect.Top + (screenRect.Height / 2))));
        region.Inflate(room, room);
        InvalidateScreenRect(region);
    }

    /// <summary>
    /// The follow bands, widened only when a guide pair is on screen. A pair's distance pill
    /// rides the cursor's own row or column but sits far from it along that axis, so the
    /// ordinary 16 px crosshair band leaves the old pill behind. Widening unconditionally
    /// would cost 1.3 Mpx a frame at 4K for a pill that is usually not there at all.
    /// </summary>
    private void InvalidateMeasureFollow(SD.Point from, SD.Point to)
    {
        bool chasing = _measure.GuidesX.Count > 1 || _measure.GuidesY.Count > 1;
        if (!chasing)
        {
            InvalidateFollowRegion(from, to);
            return;
        }

        InvalidateWideBandAt(from);
        if (to != from)
            InvalidateWideBandAt(to);
    }

    /// <summary>The pointer moved: repaint the follow bands, and — when it crossed onto
    /// another monitor — the hint strip it left behind. PaintHintStrip only runs on the pane
    /// the cursor is on, and the strip sits centred along the bottom of its own monitor, far
    /// from the cursor row: nothing else asks the departed pane to repaint it, so it stayed on
    /// screen with a ~32 px slice punched out of it where the crossing column fell, which
    /// reads as a corrupted strip rather than a stale one — two teaching strips at once on a
    /// three-monitor desktop.</summary>
    private void FollowMoved()
    {
        if (MonitorBoundsAt(_lastFollowScreen) != MonitorBoundsAt(_currentScreen))
            InvalidateHintStripOn(_lastFollowScreen);

        InvalidateMeasureFollow(_lastFollowScreen, _currentScreen);
        _lastFollowScreen = _currentScreen;
    }

    private void InvalidateWideBandAt(SD.Point pt)
    {
        SD.Rectangle monitor = MonitorBoundsAt(pt);
        int room = MeasurePillRoom(WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitor));
        InvalidateScreenRect(new SD.Rectangle(
            pt.X - room, monitor.Top, room * 2, monitor.Height));
        InvalidateScreenRect(new SD.Rectangle(
            monitor.Left, pt.Y - room, monitor.Width, room * 2));
    }

    /// <summary>Returns true when the key was a measurement gesture and the ordinary
    /// selector handling must not also run. Auto-repeat never reaches the gestures: a key
    /// this mode claimed on the way down stays claimed, silently, until it is released.</summary>
    private bool HandleMeasureKeyDown(WF.KeyEventArgs e)
    {
        if (_measureKeysDown.Contains(e.KeyCode))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }

        if (!HandleMeasureKeyDownCore(e))
            return false;

        if (RepeatsAreOnePress(e.KeyCode))
            _measureKeysDown.Add(e.KeyCode);
        return true;
    }

    /// <summary>The gestures a held key must not repeat: each of them places something,
    /// copies something or raises a toast, and thirty of those a second is one press counted
    /// thirty times. Escape and Backspace are left repeatable on purpose — the second Escape
    /// IS a second press, a key-up lost to a focus change must never strand the user in the
    /// mode, and repeat-to-undo is what Backspace does everywhere else on Windows.</summary>
    private static bool RepeatsAreOnePress(WF.Keys key) => key is
        WF.Keys.H or WF.Keys.V or WF.Keys.Enter or WF.Keys.C or WF.Keys.ShiftKey or WF.Keys.Menu;

    private bool HandleMeasureKeyDownCore(WF.KeyEventArgs e)
    {
        bool shift = (e.Modifiers & WF.Keys.Shift) == WF.Keys.Shift;
        bool ctrl = (e.Modifiers & WF.Keys.Control) == WF.Keys.Control;

        switch (e.KeyCode)
        {
            case WF.Keys.H when shift && !ctrl:
                AddGuideY(SnapCoordinate(_currentScreen.Y, _snapEdgesY));
                Handled(e);
                return true;

            case WF.Keys.V when shift && !ctrl:
                AddGuideX(SnapCoordinate(_currentScreen.X, _snapEdgesX));
                Handled(e);
                return true;

            // All four guarded on !ctrl — the guide pair above included: Ctrl+V is paste
            // everywhere else on Windows, Ctrl+Shift+C is already a real binding two cases
            // down, and this modifier space is not free enough to leave half-claimed.
            case WF.Keys.H when !ctrl && _measured is SD.Rectangle wide:
                HoldSpan(wide, horizontal: true, _currentScreen);
                Handled(e);
                return true;

            case WF.Keys.V when !ctrl && _measured is SD.Rectangle tall:
                HoldSpan(tall, horizontal: false, _currentScreen);
                Handled(e);
                return true;

            case WF.Keys.H when !ctrl:
            case WF.Keys.V when !ctrl:
                NothingToMeasureYet();
                Handled(e);
                return true;

            case WF.Keys.Back:
                // The newest THING, hold or guide. A user who has just placed a guide has
                // plainly done something undoable, and telling them otherwise while the only
                // other way out is Escape — which takes every hold and every guide with it —
                // is the mode charging for one slip twice.
                if (_measure.RemoveLast())
                {
                    Handled(e);
                }
                else if (_measure.RestoreCleared())
                {
                    // Escape IS that slip, and it takes everything at once. One press of the
                    // undo key brings the whole canvas back rather than nothing at all.
                    MeasureToast("Measurements restored.", ToastSeverity.Success);
                    Handled(e);
                }
                else
                {
                    MeasureToast("Nothing to undo.");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                return true;

            case WF.Keys.C when ctrl && shift:
                CopyPixelColor(_currentScreen);
                Handled(e);
                return true;

            case WF.Keys.C when ctrl:
                CopyMeasurement(_measured, _currentScreen);
                Handled(e);
                return true;

            case WF.Keys.Apps:
            case WF.Keys.F10 when shift:
                // The menu is where the mode explains itself, and every other gesture here is
                // keyboard-driven; reaching it should not require a mouse.
                ShowMeasureMenu(_currentScreen);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;

            case WF.Keys.Escape:
                // First press clears the canvas, second one leaves. Anything else would make
                // an hour of measuring one slip away from gone.
                if (_measure.HasAnything)
                {
                    ClearMeasurements();
                    Handled(e);
                    return true;
                }
                return false; // let the ordinary Escape close the selector

            case WF.Keys.Enter when _measured is SD.Rectangle target:
                // No repaint to ask for: this ends the session.
                e.Handled = true;
                e.SuppressKeyPress = true;
                CaptureMeasured(target);
                return true;

            case WF.Keys.Enter:
                NothingToMeasureYet();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;

            case WF.Keys.ShiftKey:
                // Shift locks the readout to one axis; the paint reads ModifierKeys, so all
                // this has to do is ask for one repaint — not one per key-repeat. The pill
                // changes SHAPE though, from the three-line cross to a one-line span, so the
                // hot state has to be re-derived or the hand cursor is left lit on ground
                // that is no longer the pill. Key-repeat is already gone by here.
                RefreshPillHot();
                Handled(e);
                return true;

            case WF.Keys.Menu:
                // Alt is the contrast-scale modifier here, not the magnifier latch: in this
                // mode the loupe is part of the tool and must not blink out under the hand
                // that is reaching for Alt+click.
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
        }

        return false;
    }

    /// <summary>Returns true when the key-up was a measurement gesture. Releasing is what
    /// re-arms the key: until it arrives, every repeat of it is swallowed.</summary>
    private bool HandleMeasureKeyUp(WF.KeyEventArgs e)
    {
        if (!_measureKeysDown.Remove(e.KeyCode))
            return false;

        // Letting go of Shift puts the second axis back, so the cross has to be redrawn —
        // and the pill it is drawn in changes shape again, so re-derive the hot state too.
        if (e.KeyCode == WF.Keys.ShiftKey)
        {
            RefreshPillHot();
            InvalidateAllSurfaces();
        }
        return true;
    }

    /// <summary>
    /// Places a guide and says so when it lands on one that is already there. Both the key
    /// and the menu row come through here: the repaint after a rejected insert paints an
    /// identical frame, so without this the one gesture in the mode with no answer is the one
    /// the user is most likely to repeat — two Shift+V presses inside the same window edge's
    /// snap band both resolve to the same column.
    /// </summary>
    private void AddGuideX(int x)
    {
        if (!_measure.AddGuideX(x))
            MeasureToast("There is already a guide on that line.");
    }

    private void AddGuideY(int y)
    {
        if (!_measure.AddGuideY(y))
            MeasureToast("There is already a guide on that line.");
    }

    /// <summary>Freezes a span and says so when it lands on one already held — the same
    /// answer, through the same channel, as a guide landing on a guide. Both the key and the
    /// menu row come through here, because a repeat of either paints an identical frame.</summary>
    private void HoldSpan(SD.Rectangle bounds, bool horizontal, SD.Point at)
    {
        if (!_measure.Hold(bounds, horizontal, at, MonitorDpiAt(at)))
            MeasureToast("That span is already held.");
    }

    /// <summary>Escape's wipe, and the menu row that does the same thing. The destructive
    /// half used to be the silent one while the recovery half toasted — backwards, and the
    /// strip's "Backspace removes the last one" does not read as "Backspace brings it all
    /// back". Both paths say it now.</summary>
    private void ClearMeasurements()
    {
        if (_measure.ClearOnEscape())
            MeasureToast("Measurements cleared. Backspace restores them.");
    }

    /// <summary>Every message this mode raises, on the monitor being measured. Measure is a
    /// per-monitor tool driven from the cursor, and the tray-corner default reports a failure
    /// on a screen the user is not looking at — this machine runs two monitors above the
    /// laptop. The top edge, because the hint strip owns the bottom.</summary>
    private void MeasureToast(string message, ToastSeverity severity = ToastSeverity.Info)
    {
        SD.Rectangle monitor = MonitorBoundsAt(_currentScreen);
        App.Toast("Measure", message,
            anchor: new SD.Point(monitor.Left + (monitor.Width / 2), monitor.Top),
            severity: severity);
    }

    /// <summary>The one answer for every gesture that arrived before there was anything to
    /// act on — which, until the freeze snapshot lands, is all of them. Saying nothing is the
    /// one thing a mode whose hint strip advertises six gestures cannot do.</summary>
    private void NothingToMeasureYet() => MeasureToast(NothingToMeasureMessage,
        // A failed snapshot is the same condition the freeze path already reported as an
        // error; it must not change glyph depending on which key the user pressed next.
        severity: _freezeFailed ? ToastSeverity.Error : ToastSeverity.Info);

    internal string NothingToMeasureMessage =>
        _frozen is not null ? "Nothing to measure here."
        : _freezeFailed ? "The screen could not be read, so there is nothing to measure. Press Esc to close."
        : "Still preparing — the screen snapshot has not landed yet.";

    private void Handled(WF.KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        InvalidateAllSurfaces();
    }

    private void HandleMeasureMouseDown(WF.MouseEventArgs e, SD.Point screen)
    {
        if (e.Button == WF.MouseButtons.Right)
        {
            // A guide grabbed a moment ago is dropped where it stands, because the drop-down
            // about to open is modal and eats the left mouse-up that would otherwise end the
            // drag — without this the guide stays glued to the pointer for the rest of the
            // session, with no button held and no way to put it down.
            _draggingGuide = null;

            // Right-click-cancels stays trained in Area and Window; here it is the menu,
            // because nothing else in this mode is discoverable by looking at it.
            ShowMeasureMenu(screen);
            return;
        }

        if (e.Button != WF.MouseButtons.Left)
            return;

        if ((WF.Control.ModifierKeys & WF.Keys.Alt) == WF.Keys.Alt)
        {
            ToggleContrastScale();
            return;
        }

        // Two targets, two meanings, and the difference is visible before the click: the
        // pill is the only thing under the pointer that lights up, and it is the one that
        // captures. Everywhere else — which is the object itself — copies. The pill is
        // parked off the object and holds still while the pointer walks to it, so nothing
        // that ends the session can ever land under a pointer that was only measuring.
        //
        // It is hit-tested BEFORE the guides because MeasureCursor tests it first, and the
        // two have to agree: a guide is a full-bleed line, grabbable anywhere along its own
        // coordinate, so any vertical guide crossing the pill would otherwise turn the one
        // lit, hand-cursored target in this mode into the start of a guide drag.
        if (_measured is SD.Rectangle onPill &&
            MeasurePillScreenRect() is SD.Rectangle target && target.Contains(screen))
        {
            CaptureMeasured(onPill);
            return;
        }

        // A guide under the pointer is a thing to drag, and it wins over the copy below.
        int? guideX = _measure.GuideNear(vertical: true, screen.X, GuideGrabPx);
        if (guideX is int gx)
        {
            _draggingGuide = (true, gx);
            return;
        }
        int? guideY = _measure.GuideNear(vertical: false, screen.Y, GuideGrabPx);
        if (guideY is int gy)
        {
            _draggingGuide = (false, gy);
            return;
        }

        if (_measured is not SD.Rectangle run)
        {
            NothingToMeasureYet();
            return;
        }

        CopyMeasurement(run, screen);
    }

    private void HandleMeasureWheel(WF.MouseEventArgs e)
    {
        if (!_measure.AdjustTolerance(e.Delta))
        {
            // Both ends of the knob. Everything else in this mode answers every gesture, and
            // a wheel that does nothing at all reads as a dead wheel rather than as an end —
            // so replay the strip's own entrance, which is where the number lives.
            //
            // Only when it is not already replaying. A real wheel delivers notches every
            // 20-50 ms and the entrance is 280 ms, so restarting on each one held the strip
            // near zero for as long as the wheel turned — flickering out the very number the
            // gesture is about. And it dips to HintPulseFloor rather than out: the copy has
            // not changed, so there is nothing to cross-fade to, and this is the one replay
            // a user can trigger a dozen times in a row.
            if (_hintStripTween is null)
                RestartHintEntrance(HintPulseFloor);
            return;
        }

        PersistTolerance();
        // The move handler freezes the reading while the pointer is on the pill or in the
        // corridor to it, and the wheel has to honour the same hold: the pill is parked OFF
        // the object it labels, so re-reading under the pointer would measure the desktop
        // behind the pill — and the invitation to turn this knob is exactly what walks the
        // user over there.
        bool parked = MeasurePillReach() is SD.Rectangle reach && reach.Contains(_currentScreen);
        ReadInto(parked ? _measuredAt : _currentScreen);
        RefreshPillHot();
        InvalidateAllSurfaces();
    }

    /// <summary>
    /// Flips the contrast scale and makes sure the flip is visible. The pill is the usual
    /// answer, but it is not drawn at all over flat ground (there is no contrast to report),
    /// and on a narrow monitor the hint strip collapses past the clause that names the
    /// gesture — so Alt+click and its menu row could both be completely silent. When there is
    /// no pill on screen the toast says which scale is now showing.
    /// </summary>
    private void ToggleContrastScale()
    {
        bool apca = _measure.ToggleContrastScale();
        bool visible = _measured is SD.Rectangle run && ContrastLabelFor(run) is not null;
        if (!visible)
            MeasureToast(apca ? "Showing APCA contrast." : "Showing WCAG contrast.");
        InvalidateAllSurfaces();
    }

    /// <summary>Render seam: whether the contrast readout has anything to say right now —
    /// which is what decides whether the scale toggle needs a toast of its own.</summary>
    internal bool ContrastLabelIsOnScreenForRender =>
        _measured is SD.Rectangle run && ContrastLabelFor(run) is not null;

    private WF.Cursor MeasureCursor()
    {
        if (_pillHot)
            return WF.Cursors.Hand;
        if (_draggingGuide is (bool draggingVertical, _))
            return draggingVertical ? WF.Cursors.SizeWE : WF.Cursors.SizeNS;
        if (_measure.GuideNear(vertical: true, _currentScreen.X, GuideGrabPx) is not null)
            return WF.Cursors.SizeWE;
        if (_measure.GuideNear(vertical: false, _currentScreen.Y, GuideGrabPx) is not null)
            return WF.Cursors.SizeNS;
        return WF.Cursors.Cross;
    }

    // ----------------------------------------------------------- actions

    private void CaptureMeasured(SD.Rectangle screenRect)
    {
        // The same path a click on the highlighted pane takes, so downstream refinement and
        // history treat it as the deliberate pick it is.
        SelectedByPaneClick = true;
        Confirm(VirtualFromScreen(screenRect));
    }

    /// <summary>Copies the measurement at a REMEMBERED point rather than wherever the pointer
    /// has since travelled — a menu row is clicked a long way from what it acts on.</summary>
    private void CopyMeasurement(SD.Rectangle? measured, SD.Point at)
    {
        string text = _measure.CopyText(measured, MonitorDpiAt(at));
        if (text.Length == 0)
        {
            NothingToMeasureYet();
            return;
        }
        if (TrySetClipboardText(text))
            MeasureToast(_measure.Holds.Count > 1 ? "Measurements copied." : "Dimensions copied.",
                ToastSeverity.Success);
    }

    private void CopyPixelColor(SD.Point at)
    {
        if (_frozen is null)
        {
            NothingToMeasureYet();
            return;
        }

        SD.Color color;
        try
        {
            using var snap = EdgeSnap.Lock(_frozen);
            int x = at.X - _vs.X;
            int y = at.Y - _vs.Y;
            if (x < 0 || y < 0 || x >= snap.Width || y >= snap.Height)
            {
                // Off the snapshot reads as transparent black, and "#000000" is a worse
                // answer than saying there is nothing under the pointer.
                NothingToMeasureYet();
                return;
            }
            color = snap.ColorAt(x, y);
        }
        catch (Exception ex)
        {
            Log.Error("Reading the pixel colour under the cursor failed", ex);
            MeasureToast("Could not read the pixel under the pointer.", ToastSeverity.Error);
            return;
        }

        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (TrySetClipboardText(hex))
            MeasureToast($"Copied {hex}.", ToastSeverity.Success);
    }

    private bool TrySetClipboardText(string text)
    {
        try
        {
            WF.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            // Another app can hold the clipboard open; say so rather than going quiet.
            Log.Error("Copying the measurement to the clipboard failed", ex);
            MeasureToast("Could not copy — another app is holding the clipboard.", ToastSeverity.Error);
            return false;
        }
    }

    /// <summary>Closes the menu without disposing it — see the field's note.</summary>
    private void CloseMeasureMenu()
    {
        if (_measureMenu is null || _measureMenu.IsDisposed)
            return;
        try { _measureMenu.Close(); }
        catch (Exception ex) { Log.Error("Closing the measure menu failed (non-fatal)", ex); }
    }

    /// <summary>Frees the previous menu, which by now is many messages closed.</summary>
    private void DisposePreviousMeasureMenu()
    {
        _measureMenu?.Dispose();
        _measureMenu = null;
    }

    private void ShowMeasureMenu(SD.Point screen)
    {
        DisposePreviousMeasureMenu();
        var menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkDropDown.DarkMenuRenderer(),
            ShowImageMargin = false,
        };

        void Row(string text, string shortcut, Action run, bool enabled = true)
        {
            var item = new WF.ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut, Enabled = enabled };
            item.Click += (_, _) => { run(); InvalidateAllSurfaces(); };
            menu.Items.Add(item);
        }

        // Every row acts on where the user right-clicked and on what was measured THERE, not
        // on wherever the pointer has travelled to reach the row. A drop-down does not stop
        // WM_TIMER, so the 8 ms follow tick keeps re-measuring under the open menu; without
        // these two snapshots "Hold width" freezes whatever is behind the row you clicked.
        SD.Point anchor = screen;
        SD.Rectangle? measuredAt = _measured;
        bool measuring = measuredAt is not null;

        Row("Hold width", "H",
            () => { if (measuredAt is SD.Rectangle r) HoldSpan(r, true, anchor); }, measuring);
        Row("Hold height", "V",
            () => { if (measuredAt is SD.Rectangle r) HoldSpan(r, false, anchor); }, measuring);
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Add horizontal guide", "Shift+H", () => AddGuideY(SnapCoordinate(anchor.Y, _snapEdgesY)));
        Row("Add vertical guide", "Shift+V", () => AddGuideX(SnapCoordinate(anchor.X, _snapEdgesX)));
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Copy measurement", "Ctrl+C", () => CopyMeasurement(measuredAt, anchor), measuring || _measure.Holds.Count > 0);
        Row("Copy pixel color", "Ctrl+Shift+C", () => CopyPixelColor(anchor));
        Row(_measure.UseApca ? "Show WCAG contrast" : "Show APCA contrast", "Alt+click",
            ToggleContrastScale);
        menu.Items.Add(new WF.ToolStripSeparator());
        // Three rows rather than one, because "the last thing" and "the last guide" are
        // different requests: a guide placed before a hold can only be reached by name.
        Row("Undo last", "Backspace", () => _measure.RemoveLast(), _measure.HasAnything);
        Row("Remove last hold", string.Empty, () => _measure.RemoveLastHold(), _measure.Holds.Count > 0);
        Row("Remove last guide", string.Empty, () => _measure.RemoveLastGuide(), _measure.HasGuides);
        // Named for what it actually empties: guides are not measurements, and this row is
        // the one surface that explains the difference.
        Row("Clear guides and measurements", "Esc", ClearMeasurements, _measure.HasAnything);
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Capture this object", "Enter", () => { if (measuredAt is SD.Rectangle r) CaptureMeasured(r); }, measuring);
        // Esc is a two-stage key and the copy has to say so, or the two rows read as a bug.
        Row("Exit measure mode", "Esc again", () => Complete(WF.DialogResult.Cancel));

        _measureMenu = menu;
        menu.Show(screen);
    }

    // ----------------------------------------------------------- motion (chrome only)

    /// <summary>How far down the strip dips when it replays for a gesture that did not change
    /// its copy. Never to zero: the wheel at either end of the tolerance knob replays this on
    /// every notch, and a strip that blinks fully out is the teaching surface flashing at the
    /// user for a rejected gesture.</summary>
    private const double HintPulseFloor = 0.45;

    /// <summary>The strip arriving for the first time: up from nothing.</summary>
    internal void StartHintEntrance() => RunHintEntrance(0);

    /// <summary>
    /// The strip replaying on a surface that is already painted. It eases OUT before it eases
    /// back in, because the entrance alone starts by setting the amount to the floor in one
    /// frame — an un-eased cut on the exit half of the mode's most-repeated animation, on a
    /// strip that was fully opaque a moment earlier. <see cref="Motion.EaseInCubic"/> is the
    /// mirror the vocabulary already ships for exactly this.
    /// </summary>
    private void RestartHintEntrance(double floor, Action? atTrough = null)
    {
        double from = _hintAmount;
        if (from <= floor + 0.01)
        {
            atTrough?.Invoke();
            RunHintEntrance(floor);
            return;
        }

        StopHintEntrance();
        bool finished = false;
        IDisposable tween = Motion.Run(
            Motion.QuickMs,
            Motion.EaseInCubic,
            amount =>
            {
                _hintAmount = from - ((from - floor) * amount);
                InvalidateHintStrip();
            },
            () =>
            {
                finished = true;
                _hintStripTween = null;
                // The trough: the strip is at the floor, so a copy that changes here changes
                // under cover. Whatever the caller wants swapped is swapped now, and the rise
                // below carries the new line up.
                atTrough?.Invoke();
                RunHintEntrance(floor);
            });
        // Only a tween that is still running: see RunHintEntrance.
        if (!finished)
            _hintStripTween = tween;
    }

    private void RunHintEntrance(double from)
    {
        StopHintEntrance();
        _hintAmount = from;
        bool finished = false;
        IDisposable tween = Motion.Run(
            Motion.EnterDurationMs,
            Motion.EaseOutCubic,
            amount =>
            {
                _hintAmount = from + ((1 - from) * amount);
                InvalidateHintStrip();
            },
            () => { finished = true; _hintStripTween = null; });
        // With animations off Motion.Run runs onDone and returns a finished handle, so a bare
        // assignment would park it in the field forever and "_hintStripTween is null" would stop
        // meaning idle. Keep it only while it is still running.
        if (!finished)
            _hintStripTween = tween;
    }

    /// <summary>Points the strip copy at the state the mode is actually in. Called at the
    /// trough of a re-entrance, never on the frame the state changed.</summary>
    private void LatchHintCopy()
    {
        _hintCopyReady = _frozen is not null;
        _hintCopyFailed = _freezeFailed;
    }

    /// <summary>Render seam: the copy the strip is painting right now, latch and all.</summary>
    internal string PaintedHintCopy() => HintStripText();

    internal void StopHintEntrance()
    {
        _hintStripTween?.Dispose();
        _hintStripTween = null;
    }

    private void InvalidateHintStrip() => InvalidateHintStripOn(_currentScreen);

    /// <summary>The strip ground on the monitor <paramref name="cursor"/> is on, worked out
    /// with the strip geometry for THAT cursor — the loupe lifts the strip clear, so where it
    /// sits depends on where the pointer was, which matters when the pointer has since left.</summary>
    private void InvalidateHintStripOn(SD.Point cursor)
    {
        // Measured, not guessed: the strip wraps onto as many lines as the monitor needs and
        // lifts clear of the loupe, so a fixed band along the bottom stopped describing it.
        SD.Rectangle monitor = MonitorBoundsAt(cursor);
        SD.Rectangle strip = HintStripRect(monitor, cursor);
        strip.Offset(monitor.Location);
        strip.Inflate(8, 12); // room for the 8 px entrance rise and the capsule's ends
        InvalidateScreenRect(strip);
    }

    // ----------------------------------------------------------- painting

    private int MonitorDpiAt(SD.Point screen) =>
        (int)Math.Round(96 * WinShot.Recording.RecordingMonitorDpi.ScaleFor(MonitorBoundsAt(screen)));

    private static bool AxisLocked => (WF.Control.ModifierKeys & WF.Keys.Shift) == WF.Keys.Shift;

    /// <summary>Which single axis Shift locks to: the longer span, because that is the one
    /// the eye was already on.</summary>
    private static bool LockedToWidth(SD.Rectangle run) => run.Width >= run.Height;

    private string MeasureLiveLabel(SD.Rectangle run) => MeasureLiveLabel(run, AxisLocked);

    private string MeasureLiveLabel(SD.Rectangle run, bool locked)
    {
        int dpi = MonitorDpiAt(_currentScreen);
        if (!locked)
            return MeasureFormat.CrossLabel(run.Size, dpi);
        return MeasureFormat.SpanLabel(LockedToWidth(run) ? run.Width : run.Height, dpi);
    }

    /// <summary>Render seam: the live pill's text with the axis lock forced either way,
    /// because a test cannot hold Shift down.</summary>
    internal string LiveMeasurementLabelForRender(bool axisLocked) =>
        _measured is SD.Rectangle run ? MeasureLiveLabel(run, axisLocked) : string.Empty;

    /// <summary>
    /// Where the live pill lands, in screen px. Paint and hit-test both come through here —
    /// clicking the pill captures the object and clicking the object copies its dimensions,
    /// so the two must agree on the pixel.
    ///
    /// It is anchored to the RUN's own edge rather than to the cursor's row, which is what
    /// makes it a target at all: a pill placed relative to the pointer keeps its distance
    /// from the pointer forever and can never be reached. Parked just outside the object it
    /// labels, it holds still for as long as the pointer stays on that object.
    ///
    /// A run that touches both the top and the bottom of the monitor has no outside to park
    /// in — which is not the exotic case it sounds like, because it takes only 44 px of slack
    /// at each edge to rule both out, and the empty gap column beside a window is a full-height
    /// run. So the fallback parks INSIDE the run, at a fixed monitor-relative row. Fixed is
    /// the whole point: an earlier take anchored it to the cursor's row, which meant the pill
    /// moved every time the pointer stepped toward it, could never be entered, never lit, never
    /// showed the hand cursor and never took the click the hint strip promises it takes.
    /// </summary>
    private SD.Rectangle? MeasurePillScreenRect()
    {
        if (_measured is not SD.Rectangle run)
            return null;

        SD.Rectangle monitor = MonitorBoundsAt(_currentScreen);
        SD.Size pill = SelectorChrome.MeasureLabel(
            MeasureLiveLabel(run), WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitor));
        int centre = (run.Left + run.Right - 1) / 2;
        int left = Math.Clamp(centre - (pill.Width / 2), monitor.Left, Math.Max(monitor.Left, monitor.Right - pill.Width));
        int gap = SelectorChrome.DimensionPillGap;

        int aboveRun = run.Top - pill.Height - gap;
        if (aboveRun >= monitor.Top)
            return new SD.Rectangle(left, aboveRun, pill.Width, pill.Height);

        int belowRun = run.Bottom + gap;
        if (belowRun + pill.Height <= monitor.Bottom)
            return new SD.Rectangle(left, belowRun, pill.Width, pill.Height);

        int inside = Math.Clamp(run.Top + gap, monitor.Top,
            Math.Max(monitor.Top, monitor.Bottom - pill.Height));
        return new SD.Rectangle(left, inside, pill.Width, pill.Height);
    }

    /// <summary>
    /// The pill plus the corridor between it and the edge of the run it labels — the ground
    /// the pointer crosses on its way to the pill, and the ground on which the reading holds
    /// still so that crossing arrives somewhere. Pill-wide and one gap tall; when the pill had
    /// to park inside the run this is just the pill, which is enough — the pointer is already
    /// on the run, so walking to the pill never changes the reading anyway.
    /// </summary>
    private SD.Rectangle? MeasurePillReach()
    {
        if (_measured is not SD.Rectangle run || MeasurePillScreenRect() is not SD.Rectangle pill)
            return null;

        if (pill.Bottom <= run.Top)
            return SD.Rectangle.FromLTRB(pill.Left, pill.Top, pill.Right, run.Top);
        if (pill.Top >= run.Bottom)
            return SD.Rectangle.FromLTRB(pill.Left, run.Bottom, pill.Right, pill.Bottom);
        return pill;
    }

    /// <summary>Render seam: the pill's reach, and whether the pointer is on it.</summary>
    internal SD.Rectangle? LivePillReachForRender => MeasurePillReach();

    internal bool PillIsHotForRender => _pillHot;

    internal double PillHoverForRender => _measurePillHover;

    /// <summary>The pill is chrome, not a reading, so it may animate: functional data in this
    /// mode never does. Cross-fades the hover outline over <see cref="Motion.QuickMs"/> from
    /// wherever it currently sits, so a pointer skimming in and out does not snap.</summary>
    private void SetPillHot(bool hot)
    {
        if (_pillHot == hot)
            return;

        _pillHot = hot;
        StopPillHover();
        double from = _measurePillHover;
        double to = hot ? 1 : 0;
        _measurePillHoverTween = Motion.Run(
            Motion.QuickMs,
            Motion.EaseOutCubic,
            amount =>
            {
                _measurePillHover = from + ((to - from) * amount);
                if (_measured is SD.Rectangle hovered)
                    InvalidateMeasureRegion(hovered);
            },
            () => _measurePillHoverTween = null);
    }

    internal void StopPillHover()
    {
        _measurePillHoverTween?.Dispose();
        _measurePillHoverTween = null;
    }

    private void PaintMeasureSurface(SD.Graphics g, SD.Rectangle monitorBounds, bool cursorOnThisSurface)
    {
        SD.Size clientSize = monitorBounds.Size;
        double scale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds);
        PaintFrozenOrDim(g, monitorBounds);

        SD.Color crimson = ThemePalette.Measure;
        SD.Color guideBlue = ThemePalette.Accent;

        // Z-order is ground, then data, then chrome. Brightening repaints the frozen slice
        // inside the run, so anything drawn before it comes back cut in half wherever it
        // crossed the measured object — guides and reticle both.
        if (cursorOnThisSurface && _measured is SD.Rectangle bright)
            BrightenRegion(g, monitorBounds, ToLocal(bright, monitorBounds));

        foreach (int x in _measure.GuidesX)
            SelectorChrome.DrawGuide(g, clientSize, vertical: true, x - monitorBounds.X, guideBlue);
        foreach (int y in _measure.GuidesY)
            SelectorChrome.DrawGuide(g, clientSize, vertical: false, y - monitorBounds.Y, guideBlue);

        PaintGuidePairs(g, monitorBounds, guideBlue);
        PaintHolds(g, monitorBounds, crimson);

        if (cursorOnThisSurface)
        {
            SelectorChrome.DrawCrosshair(g, clientSize, ToLocal(_currentScreen, monitorBounds), scale, crimson);

            if (_measured is SD.Rectangle measured)
                PaintLiveMeasurement(g, monitorBounds, measured, crimson, scale);

            if (_magnifierOn)
            {
                FastSelectorLoupeRenderer.Draw(
                    g, clientSize, _vs, ToLocal(_currentScreen, monitorBounds), _currentScreen, _frozen);
            }

            PaintHintStrip(g, monitorBounds);
        }
    }

    private void PaintLiveMeasurement(
        SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle run, SD.Color crimson, double scale)
    {
        SD.Size clientSize = monitorBounds.Size;
        int row = Math.Clamp(_currentScreen.Y, run.Top, run.Bottom - 1);
        int column = Math.Clamp(_currentScreen.X, run.Left, run.Right - 1);
        bool locked = AxisLocked;
        bool width = LockedToWidth(run);

        if (!locked || width)
        {
            SelectorChrome.DrawDimension(g, clientSize,
                ToLocal(new SD.Point(run.Left, row), monitorBounds),
                ToLocal(new SD.Point(run.Right - 1, row), monitorBounds),
                label: null, crimson, scale);
        }
        if (!locked || !width)
        {
            SelectorChrome.DrawDimension(g, clientSize,
                ToLocal(new SD.Point(column, run.Top), monitorBounds),
                ToLocal(new SD.Point(column, run.Bottom - 1), monitorBounds),
                label: null, crimson, scale);
        }

        // One pill for the live reading: "W x H" for the cross, a bare integer when Shift
        // locks it to one axis. Placed by MeasurePillScreenRect so the click that captures
        // the object lands on exactly the pixels that were drawn.
        // Contrast first: it yields to the live pill, and the live pill is the primary datum,
        // so the primary datum is also the one drawn last.
        PaintContrastReadout(g, monitorBounds, run);

        if (MeasurePillScreenRect() is SD.Rectangle pill)
        {
            SD.Point local = ToLocal(pill.Location, monitorBounds);
            SD.Color? outline = _measurePillHover > 0.01
                ? SD.Color.FromArgb((int)Math.Round(255 * Math.Clamp(_measurePillHover, 0, 1)), crimson)
                : null;
            SelectorChrome.DrawLabel(g, clientSize, MeasureLiveLabel(run), local.X, local.Y,
                scale, 1d, fullyRounded: true, outline: outline);
        }
    }

    /// <summary>The contrast of the two colours the measured region is made of, above it.
    /// Alt+click flips between WCAG's ratio and APCA's Lc — they answer different questions
    /// and the label always says which one is showing.</summary>
    private void PaintContrastReadout(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle run)
    {
        if (ContrastLabelFor(run) is not string label)
            return;
        if (ContrastPillScreenRect(monitorBounds, run) is not SD.Rectangle pill)
            return;

        SD.Point at = ToLocal(pill.Location, monitorBounds);
        SelectorChrome.DrawLabel(g, monitorBounds.Size, label, at.X, at.Y,
            WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds), 1d, fullyRounded: true);
    }

    private string? ContrastLabelFor(SD.Rectangle run)
    {
        if (_contrast is not (SD.Color background, SD.Color foreground) || _contrastFor != run)
            return null;
        if (background.ToArgb() == foreground.ToArgb())
            return null; // a flat region has no contrast to report, and "1.0 : 1" is noise
        return ColorMath.ContrastLabel(foreground, background, _measure.UseApca);
    }

    /// <summary>
    /// Where the contrast pill lands. It wants to sit above the measured region, but the live
    /// pill is placed from the CURSOR's row inside that region — so for any cursor near the
    /// top of a small object the two want the same pixels, and they are the same ground colour:
    /// an overlap reads as one blob with two texts in it. The contrast readout is the one that
    /// yields, because the live reading is what the mode exists to show.
    /// </summary>
    private SD.Rectangle? ContrastPillScreenRect(SD.Rectangle monitorBounds, SD.Rectangle run)
    {
        if (ContrastLabelFor(run) is not string label)
            return null;

        SD.Size pill = SelectorChrome.MeasureLabel(
            label, WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));
        int gap = SelectorChrome.DimensionPillGap;
        SD.Rectangle? live = MeasurePillScreenRect();

        int above = run.Top - pill.Height - gap;
        if (live is SD.Rectangle livePill)
            above = Math.Min(above, livePill.Top - pill.Height - gap);

        int top;
        if (above >= monitorBounds.Top)
        {
            top = above;
        }
        else
        {
            // No room above: go below whichever of the two sits lower, never on top of it.
            top = run.Top + gap;
            if (live is SD.Rectangle below)
                top = Math.Max(top, below.Bottom + gap);
            top = Math.Clamp(top, monitorBounds.Top, Math.Max(monitorBounds.Top, monitorBounds.Bottom - pill.Height));
        }

        int left = Math.Clamp(run.Left, monitorBounds.Left,
            Math.Max(monitorBounds.Left, monitorBounds.Right - pill.Width));
        return new SD.Rectangle(left, top, pill.Width, pill.Height);
    }

    /// <summary>
    /// Whether a measurement covering <paramref name="screenRect"/> can put ink anywhere in
    /// the invalid region this paint was asked for. Every repaint in this mode is a band — the
    /// 8 ms follow tick asks for one on every tick — and a dimension is expensive: two label
    /// measures, two GraphicsPaths and a GDI-interop text draw, ~2.5 ms each. Without this
    /// every hold and every guide pair on the monitor paid that on every frame, so the cost
    /// of the canvas grew with the canvas and the clip bought nothing.
    ///
    /// The margin is <see cref="MeasurePillRoom"/>, the same inflation
    /// <see cref="InvalidateMeasureRegion"/> asks for, so the cull cannot be tighter than the
    /// invalidation: anything this skips could not have been in the invalid region.
    /// </summary>
    internal static bool TouchesClip(SD.Graphics g, SD.Rectangle screenRect, SD.Rectangle monitorBounds)
    {
        SD.Rectangle local = ToLocal(screenRect, monitorBounds);
        int room = MeasurePillRoom(WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));
        local.Inflate(room, room);
        return local.IntersectsWith(ClipRect(g, monitorBounds.Size));
    }

    /// <summary>The invalid region as a pane-local rect, clamped to the pane. GDI+ reports an
    /// unclipped surface as a rect of floats far outside int range, so this cannot go through
    /// Rectangle.Ceiling.</summary>
    internal static SD.Rectangle ClipRect(SD.Graphics g, SD.Size clientSize)
    {
        SD.RectangleF c = g.ClipBounds;
        int left = (int)Math.Clamp(Math.Floor(c.Left), 0, clientSize.Width);
        int top = (int)Math.Clamp(Math.Floor(c.Top), 0, clientSize.Height);
        int right = (int)Math.Clamp(Math.Ceiling(c.Right), 0, clientSize.Width);
        int bottom = (int)Math.Clamp(Math.Ceiling(c.Bottom), 0, clientSize.Height);
        return SD.Rectangle.FromLTRB(left, top, right, bottom);
    }

    private void PaintHolds(SD.Graphics g, SD.Rectangle monitorBounds, SD.Color crimson)
    {
        double scale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds);
        foreach (MeasureHold hold in _measure.Holds)
        {
            SD.Rectangle bounds = hold.Bounds;
            if (!bounds.IntersectsWith(monitorBounds) || !TouchesClip(g, bounds, monitorBounds))
                continue;

            // The hold's own DPI, never the cursor's and never the pane's: a span held on a
            // 96 dpi secondary must not be relabelled in the 150 % primary's dip once the
            // pointer walks back over there, or once it is drawn across a pane boundary —
            // and the clipboard reads the same field, so the two cannot disagree.
            int dpi = hold.Dpi;
            if (hold.Horizontal)
            {
                int row = Math.Clamp(hold.At.Y, bounds.Top, bounds.Bottom - 1);
                SelectorChrome.DrawDimension(g, monitorBounds.Size,
                    ToLocal(new SD.Point(bounds.Left, row), monitorBounds),
                    ToLocal(new SD.Point(bounds.Right - 1, row), monitorBounds),
                    MeasureFormat.SpanLabel(bounds.Width, dpi), crimson, scale);
            }
            else
            {
                int column = Math.Clamp(hold.At.X, bounds.Left, bounds.Right - 1);
                SelectorChrome.DrawDimension(g, monitorBounds.Size,
                    ToLocal(new SD.Point(column, bounds.Top), monitorBounds),
                    ToLocal(new SD.Point(column, bounds.Bottom - 1), monitorBounds),
                    MeasureFormat.SpanLabel(bounds.Height, dpi), crimson, scale);
            }
        }
    }

    /// <summary>Two parallel guides label the gap between them, with the pill riding the
    /// cursor's end of the span — you read the number where you are looking. No T-serifs:
    /// §3.1 allows a pair its pill but not the caps, because a cap is crimson's claim that
    /// the app found that edge, and here the user put both ends there themselves. The absent
    /// caps are also the non-colour half of the difference, which blue and crimson need —
    /// they are only 1.27:1 apart in luminance.</summary>
    private void PaintGuidePairs(SD.Graphics g, SD.Rectangle monitorBounds, SD.Color guideBlue)
    {
        // The CURSOR's monitor, not the painting pane's. The pill rides the cursor's own row
        // or column, so a pair straddling a 96 dpi secondary and a 150 % primary was reading
        // one dip figure on one pane and a different one on the other for the same gap.
        int dpi = MonitorDpiAt(_currentScreen);
        double scale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds);
        SD.Point cursor = _currentScreen;

        // The same margin the hint strip keeps off its own edge, scaled the same way: a pair's
        // pill must not end up flush to the screen. One number, one rule, one place.
        int inset = HintStripGap(monitorBounds);

        // to - from, not the holds' inclusive width, and deliberately: a guide snaps through
        // SnapCoordinate onto _snapEdgesX/Y, which are window Left and Right — and Right is
        // exclusive. So a pair snapped to the two edges of a window reads exactly that
        // window's width, which is the same number the hold over it reads.
        foreach (var (from, to) in MeasureSession.Pairs(_measure.GuidesX))
        {
            int y = Math.Clamp(cursor.Y, monitorBounds.Top + inset, monitorBounds.Bottom - inset);
            if (!TouchesClip(g, SD.Rectangle.FromLTRB(from, y, to + 1, y + 1), monitorBounds))
                continue;
            SelectorChrome.DrawDimension(g, monitorBounds.Size,
                ToLocal(new SD.Point(from, y), monitorBounds),
                ToLocal(new SD.Point(to, y), monitorBounds),
                MeasureFormat.SpanLabel(to - from, dpi), guideBlue, scale, serifs: false);
        }

        foreach (var (from, to) in MeasureSession.Pairs(_measure.GuidesY))
        {
            int x = Math.Clamp(cursor.X, monitorBounds.Left + inset, monitorBounds.Right - inset);
            if (!TouchesClip(g, SD.Rectangle.FromLTRB(x, from, x + 1, to + 1), monitorBounds))
                continue;
            SelectorChrome.DrawDimension(g, monitorBounds.Size,
                ToLocal(new SD.Point(x, from), monitorBounds),
                ToLocal(new SD.Point(x, to), monitorBounds),
                MeasureFormat.SpanLabel(to - from, dpi), guideBlue, scale, serifs: false);
        }
    }

    private const string HintSeparator = "  ·  ";

    /// <summary>Everything this mode can do, in reading order. Alt-toggles-magnifier is
    /// undiscoverable in the ordinary selector and always has been; measurement has nine
    /// gestures and cannot afford the same silence.</summary>
    private string[] HintStripItems() => new[]
    {
        // The wheel was the one gesture the strip printed the RESULT of without ever naming
        // the control: "Tolerance 30" says what the knob is set to and nothing about there
        // being a knob. Same width class, so the wrap is unchanged.
        $"Wheel: tolerance {_measure.Tolerance}",
        "H / V hold",
        "Shift+H / Shift+V guide",
        "Click or Ctrl+C copies",
        "Click the pill or Enter captures",
        $"Alt+click for {(_measure.UseApca ? "APCA" : "WCAG")} contrast",
        "Backspace removes the last one",
        "Right-click for more",
        "Esc clears, Esc again exits",
    };

    /// <summary>The gestures as one line. This is the copy, not the layout — what actually
    /// paints comes from <see cref="HintStripTextFor"/>, which wraps it to the monitor.</summary>
    internal string HintStripText()
    {
        // Until the snapshot lands there is nothing to measure, and advertising nine gestures
        // that all answer "not yet" is worse than saying so. A loading state is a surface —
        // and so is a failed one: the error toast dwells out after a few seconds, and after
        // that the strip is the only thing left to say the mode cannot work and how to leave.
        if (!_hintCopyReady)
        {
            return _hintCopyFailed
                ? "The screen could not be read, so there is nothing to measure  ·  Esc closes"
                : "Preparing the screen snapshot…";
        }

        return string.Join(HintSeparator, HintStripItems());
    }

    /// <summary>
    /// The strip wrapped to fit <paramref name="monitorWidth"/> physical px. Nine
    /// separator-joined clauses is 227 characters, which is 1824 px at 144 dpi and 2456 at
    /// 192 — wider than most panels, and unscannable even where it fits. The items pack
    /// greedily onto as many lines as the monitor needs, so every gesture the strip exists to
    /// teach survives at every DPI instead of collapsing to a two-item stub. Three lines is
    /// the ceiling — past that the strip is a paragraph, and a surface that tall along the
    /// bottom edge is worse than the short line that points at the menu.
    /// </summary>
    internal string HintStripTextFor(int monitorWidth, double scale = 1d)
    {
        if (!_hintCopyReady)
            return HintStripText();

        // The wrap costs ~0.5 ms — up to sixteen font measures — and it was being run two or
        // three times per paint and once per frame of the entrance tween, for an answer that
        // only changes when the tolerance number, the contrast word or the monitor changes.
        // The one-line copy is the key: it is cheap to build and it moves whenever any of
        // those do, so no cache invalidation can be forgotten.
        string source = string.Join(HintSeparator, HintStripItems());
        if (_hintWrap is (string had, int forWidth, string wrapped) && forWidth == monitorWidth && had == source)
            return wrapped;

        string packed = PackHintStrip(monitorWidth, scale);
        _hintWrap = (source, monitorWidth, packed);
        return packed;
    }

    private (string Source, int Width, string Wrapped)? _hintWrap;

    private string PackHintStrip(int monitorWidth, double scale)
    {
        const int MaxLines = 3;
        var lines = new List<string>();
        string line = string.Empty;
        foreach (string item in HintStripItems())
        {
            if (line.Length == 0)
            {
                line = item;
                continue;
            }
            string wider = line + HintSeparator + item;
            if (SelectorChrome.MeasureLabel(wider, scale).Width <= monitorWidth)
            {
                line = wider;
                continue;
            }
            lines.Add(line);
            line = item;
        }
        lines.Add(line);

        if (lines.Count <= MaxLines)
            return string.Join("\n", lines);

        // The stub is measured too. DrawLabel clamps the capsule LEFT edge only, so an
        // unmeasured stub on a pane narrower than it starts at x=0 and runs off the right.
        string stub = $"Wheel: tolerance {_measure.Tolerance}  ·  Right-click for more";
        return SelectorChrome.MeasureLabel(stub, scale).Width <= monitorWidth ? stub : "Right-click for more";
    }

    /// <summary>The strip's copy for one pane, wrapped against the width it actually gets:
    /// the monitor less the same margin it keeps at the bottom, so the capsule is not flush
    /// to both screen edges while sitting a scaled 24 px off the bottom one.</summary>
    private string HintStripTextOn(SD.Rectangle monitorBounds) =>
        HintStripTextFor(
            Math.Max(1, monitorBounds.Width - (2 * HintStripGap(monitorBounds))),
            WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));

    private static int HintStripGap(SD.Rectangle monitorBounds) => (int)Math.Round(
        HintStripBottomGap * WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));

    /// <summary>Where the strip sits on one pane, in that pane's local px. Centred along the
    /// bottom, and lifted clear of the loupe when the two want the same ground: the loupe is
    /// forced on in this mode because it is part of the tool, so chrome does not get to paint
    /// over its coordinates and its hex swatch.</summary>
    private SD.Rectangle HintStripRect(SD.Rectangle monitorBounds) =>
        HintStripRect(monitorBounds, _currentScreen);

    private SD.Rectangle HintStripRect(SD.Rectangle monitorBounds, SD.Point cursor)
    {
        SD.Size pill = SelectorChrome.MeasureLabel(
            HintStripTextOn(monitorBounds),
            WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));
        int gap = HintStripGap(monitorBounds);
        int left = Math.Max(0, (monitorBounds.Width - pill.Width) / 2);
        int top = monitorBounds.Height - gap - pill.Height;

        if (_magnifierOn && monitorBounds.Contains(cursor))
        {
            SD.Rectangle loupe = FastSelectorLoupeRenderer.OccupiedRect(
                monitorBounds.Size, _vs, ToLocal(cursor, monitorBounds), cursor);
            if (!loupe.IsEmpty && loupe.IntersectsWith(new SD.Rectangle(left, top, pill.Width, pill.Height)))
                top = loupe.Top - SelectorChrome.DimensionPillGap - pill.Height;
        }

        return new SD.Rectangle(left, Math.Max(0, top), pill.Width, pill.Height);
    }

    /// <summary>The strip fades and rises 8 px into place, through the shared pill so it
    /// cannot drift from the rest of the mode's chrome — and so it clamps like the rest.</summary>
    private void PaintHintStrip(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        if (_hintAmount <= 0.01)
            return;

        SD.Rectangle strip = HintStripRect(monitorBounds);
        double scale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds);
        SelectorChrome.DrawLabel(g, monitorBounds.Size, HintStripTextOn(monitorBounds),
            strip.Left, strip.Top + (int)Math.Round(8 * scale * (1 - _hintAmount)),
            scale, _hintAmount, fullyRounded: true);
    }

    /// <summary>Render seam: the strip in SCREEN space for the monitor a given cursor is on,
    /// which is the ground that has to be repainted when the pointer leaves that monitor.</summary>
    internal SD.Rectangle HintStripScreenRectForRender(SD.Point cursor)
    {
        SD.Rectangle monitor = MonitorBoundsAt(cursor);
        SD.Rectangle strip = HintStripRect(monitor, cursor);
        strip.Offset(monitor.Location);
        return strip;
    }

    /// <summary>Render seam: where the strip lands on this pane, so a test can prove it does
    /// not sit on the loupe and that the whole sentence is what paints.</summary>
    internal SD.Rectangle HintStripRectForRender(SD.Rectangle monitorBounds) =>
        HintStripRect(monitorBounds);

    /// <summary>Render seam: the ground the loupe covers on this pane.</summary>
    internal SD.Rectangle LoupeRectForRender(SD.Rectangle monitorBounds) =>
        FastSelectorLoupeRenderer.OccupiedRect(
            monitorBounds.Size, _vs, ToLocal(_currentScreen, monitorBounds), _currentScreen);
}
