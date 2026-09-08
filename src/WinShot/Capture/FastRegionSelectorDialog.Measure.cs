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

    private double _pillHover;
    private IDisposable? _pillHoverTween;

    /// <summary>The two colours the live run is made of, cached against the run it was
    /// sampled for so a stationary cursor does not re-histogram on every follow tick.</summary>
    private (SD.Color Background, SD.Color Foreground)? _contrast;
    private SD.Rectangle _contrastFor;

    /// <summary>The guide being dragged: which axis, and where it currently sits.</summary>
    private (bool Vertical, int At)? _draggingGuide;

    private IDisposable? _hintTween;
    private double _hintAmount;

    /// <summary>The freeze snapshot was attempted and did not arrive, so it never will this
    /// session. Without this there is no way to tell "not yet" from "never", and a mode whose
    /// only persistent surface says "Preparing the screen snapshot…" forever teaches the user
    /// to keep waiting for something that is not coming.</summary>
    private bool _freezeFailed;

    /// <summary>Shift is held rather than tapped, and key-repeat fires many times a second;
    /// the latch keeps the axis-lock repaint to one on the way down and one on the way up.</summary>
    private bool _shiftLatched;

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
        // an entrance, so it re-enters rather than cutting.
        StartHintEntrance();
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
        // snap on an error. The strip re-enters instead.
        StartHintEntrance();
    }

    /// <summary>Render seam: moves the measuring cursor and re-reads the pixels under it.</summary>
    internal void MoveMeasureCursorForRender(SD.Point cursor)
    {
        _currentScreen = cursor;
        _lastFollowScreen = cursor;
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
        _pillHover = 0;
        _contrast = null;
        _contrastFor = SD.Rectangle.Empty;
        _draggingGuide = null;
        _shiftLatched = false;
        _freezeFailed = false;
        _hintAmount = 0;
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
            // next mouse-move tries again.
            Log.Error("Measurement pixel read failed (non-fatal)", ex);
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
            InvalidateMeasureFollow(_lastFollowScreen, _currentScreen);
            _lastFollowScreen = _currentScreen;
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
            InvalidateMeasureFollow(_lastFollowScreen, _currentScreen);
            _lastFollowScreen = _currentScreen;
            return;
        }

        SD.Rectangle? before = _measured;
        ReadInto(_currentScreen);

        // The hover outline was earned on a pill that no longer exists: fading it out over a
        // DIFFERENT pill would light up something the pointer was never on. The fade is only
        // honest while the reading it belongs to is still the one on screen.
        if (_measured != before && _pillHover > 0)
        {
            StopPillHover();
            _pillHot = false;
            _pillHover = 0;
        }
        RefreshPillHot();

        // The crosshair, the pill and the contrast readout all move with the cursor, and the
        // run itself can jump anywhere: repaint the union of where it was and where it is,
        // plus the follow bands, rather than the whole (possibly 4K) monitor.
        InvalidateMeasureFollow(_lastFollowScreen, _currentScreen);
        _lastFollowScreen = _currentScreen;
        if (before is SD.Rectangle old)
            InvalidateMeasureRegion(old);
        if (_measured is SD.Rectangle now && now != before)
            InvalidateMeasureRegion(now);
    }

    /// <summary>Room for the tallest pill this mode draws, plus the contrast readout stacked
    /// above it. Derived rather than guessed: the live cross is three lines ("W × H", the dip
    /// value, the ratio) and the point-sized font grows with the monitor.</summary>
    private static int MeasurePillRoom =>
        _measurePillRoom ??= (SelectorChrome.MeasureLabel("0 × 0\n0 × 0\n0 : 0").Height * 2) + 32;

    /// <summary>Measured once: the font is a point size, so this cannot change within a run.</summary>
    private static int? _measurePillRoom;

    /// <summary>The ground one measurement covers: its own rect plus room for the pill and
    /// the contrast readout, either of which can sit above the line OR flip below it.</summary>
    private void InvalidateMeasureRegion(SD.Rectangle screenRect)
    {
        // Both directions: the pill is centred on the run and a three-line pill is wider than
        // a narrow run, so an 8 px horizontal margin would smear its ends.
        var region = screenRect;
        region.Inflate(MeasurePillRoom, MeasurePillRoom);
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

    private void InvalidateWideBandAt(SD.Point pt)
    {
        SD.Rectangle monitor = MonitorBoundsAt(pt);
        InvalidateScreenRect(new SD.Rectangle(
            pt.X - MeasurePillRoom, monitor.Top, MeasurePillRoom * 2, monitor.Height));
        InvalidateScreenRect(new SD.Rectangle(
            monitor.Left, pt.Y - MeasurePillRoom, monitor.Width, MeasurePillRoom * 2));
    }

    /// <summary>Returns true when the key was a measurement gesture and the ordinary
    /// selector handling must not also run.</summary>
    private bool HandleMeasureKeyDown(WF.KeyEventArgs e)
    {
        bool shift = (e.Modifiers & WF.Keys.Shift) == WF.Keys.Shift;
        bool ctrl = (e.Modifiers & WF.Keys.Control) == WF.Keys.Control;

        switch (e.KeyCode)
        {
            case WF.Keys.H when shift && !ctrl:
                _measure.AddGuideY(SnapCoordinate(_currentScreen.Y, _snapEdgesY));
                Handled(e);
                return true;

            case WF.Keys.V when shift && !ctrl:
                _measure.AddGuideX(SnapCoordinate(_currentScreen.X, _snapEdgesX));
                Handled(e);
                return true;

            // All four guarded on !ctrl — the guide pair above included: Ctrl+V is paste
            // everywhere else on Windows, Ctrl+Shift+C is already a real binding two cases
            // down, and this modifier space is not free enough to leave half-claimed.
            case WF.Keys.H when !ctrl && _measured is SD.Rectangle wide:
                _measure.Hold(wide, horizontal: true, _currentScreen, MonitorDpiAt(_currentScreen));
                Handled(e);
                return true;

            case WF.Keys.V when !ctrl && _measured is SD.Rectangle tall:
                _measure.Hold(tall, horizontal: false, _currentScreen, MonitorDpiAt(_currentScreen));
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
                else
                {
                    App.Toast("Measure", "Nothing to undo.");
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
                if (_measure.ClearOnEscape())
                {
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
                // this has to do is ask for one repaint — not one per key-repeat.
                if (!_shiftLatched)
                {
                    _shiftLatched = true;
                    Handled(e);
                }
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

    /// <summary>Returns true when the key-up was a measurement gesture.</summary>
    private bool HandleMeasureKeyUp(WF.KeyEventArgs e)
    {
        if (e.KeyCode != WF.Keys.ShiftKey || !_shiftLatched)
            return false;

        // Letting go of Shift puts the second axis back, so the cross has to be redrawn.
        _shiftLatched = false;
        InvalidateAllSurfaces();
        return true;
    }

    /// <summary>The one answer for every gesture that arrived before there was anything to
    /// act on — which, until the freeze snapshot lands, is all of them. Saying nothing is the
    /// one thing a mode whose hint strip advertises six gestures cannot do.</summary>
    private void NothingToMeasureYet() => App.Toast("Measure", NothingToMeasureMessage,
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
            _measure.ToggleContrastScale();
            InvalidateAllSurfaces();
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
            StartHintEntrance();
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
            App.Toast("Measure", _measure.Holds.Count > 1 ? "Measurements copied." : "Dimensions copied.",
                severity: ToastSeverity.Success);
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
            App.Toast("Measure", "Could not read the pixel under the pointer.",
                severity: ToastSeverity.Error);
            return;
        }

        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (TrySetClipboardText(hex))
            App.Toast("Measure", $"Copied {hex}.", severity: ToastSeverity.Success);
    }

    private static bool TrySetClipboardText(string text)
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
            App.Toast("Measure", "Could not copy — another app is holding the clipboard.",
                severity: ToastSeverity.Error);
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

        int anchorDpi = MonitorDpiAt(anchor);
        Row("Hold width", "H",
            () => { if (measuredAt is SD.Rectangle r) _measure.Hold(r, true, anchor, anchorDpi); }, measuring);
        Row("Hold height", "V",
            () => { if (measuredAt is SD.Rectangle r) _measure.Hold(r, false, anchor, anchorDpi); }, measuring);
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Add horizontal guide", "Shift+H", () => _measure.AddGuideY(SnapCoordinate(anchor.Y, _snapEdgesY)));
        Row("Add vertical guide", "Shift+V", () => _measure.AddGuideX(SnapCoordinate(anchor.X, _snapEdgesX)));
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Copy measurement", "Ctrl+C", () => CopyMeasurement(measuredAt, anchor), measuring || _measure.Holds.Count > 0);
        Row("Copy pixel color", "Ctrl+Shift+C", () => CopyPixelColor(anchor));
        Row(_measure.UseApca ? "Show WCAG contrast" : "Show APCA contrast", "Alt+click",
            () => _measure.ToggleContrastScale());
        menu.Items.Add(new WF.ToolStripSeparator());
        // Three rows rather than one, because "the last thing" and "the last guide" are
        // different requests: a guide placed before a hold can only be reached by name.
        Row("Undo last", "Backspace", () => _measure.RemoveLast(), _measure.HasAnything);
        Row("Remove last hold", string.Empty, () => _measure.RemoveLastHold(), _measure.Holds.Count > 0);
        Row("Remove last guide", string.Empty, () => _measure.RemoveLastGuide(), _measure.HasGuides);
        // Named for what it actually empties: guides are not measurements, and this row is
        // the one surface that explains the difference.
        Row("Clear guides and measurements", "Esc", () => _measure.ClearOnEscape(), _measure.HasAnything);
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Capture this object", "Enter", () => { if (measuredAt is SD.Rectangle r) CaptureMeasured(r); }, measuring);
        // Esc is a two-stage key and the copy has to say so, or the two rows read as a bug.
        Row("Exit measure mode", "Esc again", () => Complete(WF.DialogResult.Cancel));

        _measureMenu = menu;
        menu.Show(screen);
    }

    // ----------------------------------------------------------- motion (chrome only)

    internal void StartHintEntrance()
    {
        StopHintEntrance();
        _hintAmount = 0;
        _hintTween = Motion.Run(
            Motion.EnterDurationMs,
            Motion.EaseOutCubic,
            amount =>
            {
                _hintAmount = amount;
                InvalidateHintStrip();
            },
            () => _hintTween = null);
    }

    internal void StopHintEntrance()
    {
        _hintTween?.Dispose();
        _hintTween = null;
    }

    private void InvalidateHintStrip()
    {
        // Measured, not guessed: the strip wraps onto as many lines as the monitor needs and
        // lifts clear of the loupe, so a fixed band along the bottom stopped describing it.
        SD.Rectangle monitor = MonitorBoundsAt(_currentScreen);
        SD.Rectangle strip = HintStripRect(monitor);
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
    /// A run that fills the monitor top to bottom has neither an outside nor anything anyone
    /// would want to capture, so there the pill falls back to riding the cursor's row.
    /// </summary>
    private SD.Rectangle? MeasurePillScreenRect()
    {
        if (_measured is not SD.Rectangle run)
            return null;

        SD.Size pill = SelectorChrome.MeasureLabel(MeasureLiveLabel(run));
        SD.Rectangle monitor = MonitorBoundsAt(_currentScreen);
        int centre = (run.Left + run.Right - 1) / 2;
        int left = Math.Clamp(centre - (pill.Width / 2), monitor.Left, Math.Max(monitor.Left, monitor.Right - pill.Width));
        int gap = SelectorChrome.DimensionPillGap;

        int aboveRun = run.Top - pill.Height - gap;
        if (aboveRun >= monitor.Top)
            return new SD.Rectangle(left, aboveRun, pill.Width, pill.Height);

        int belowRun = run.Bottom + gap;
        if (belowRun + pill.Height <= monitor.Bottom)
            return new SD.Rectangle(left, belowRun, pill.Width, pill.Height);

        int row = Math.Clamp(_currentScreen.Y, run.Top, run.Bottom - 1);
        int above = row - pill.Height - gap;
        int top = above >= monitor.Top ? above : row + gap;
        top = Math.Clamp(top, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - pill.Height));
        return new SD.Rectangle(left, top, pill.Width, pill.Height);
    }

    /// <summary>
    /// The pill plus the corridor between it and the edge of the run it labels — the ground
    /// the pointer crosses on its way to the pill, and the ground on which the reading holds
    /// still so that crossing arrives somewhere. Pill-wide and one gap tall; when the pill
    /// had to fall back onto the cursor's row this is just the pill, and unreachable, which
    /// is the honest answer for a run that fills the screen.
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

    internal double PillHoverForRender => _pillHover;

    /// <summary>The pill is chrome, not a reading, so it may animate: functional data in this
    /// mode never does. Cross-fades the hover outline over <see cref="Motion.QuickMs"/> from
    /// wherever it currently sits, so a pointer skimming in and out does not snap.</summary>
    private void SetPillHot(bool hot)
    {
        if (_pillHot == hot)
            return;

        _pillHot = hot;
        StopPillHover();
        double from = _pillHover;
        double to = hot ? 1 : 0;
        _pillHoverTween = Motion.Run(
            Motion.QuickMs,
            Motion.EaseOutCubic,
            amount =>
            {
                _pillHover = from + ((to - from) * amount);
                if (_measured is SD.Rectangle hovered)
                    InvalidateMeasureRegion(hovered);
            },
            () => _pillHoverTween = null);
    }

    internal void StopPillHover()
    {
        _pillHoverTween?.Dispose();
        _pillHoverTween = null;
    }

    private void PaintMeasureSurface(SD.Graphics g, SD.Rectangle monitorBounds, bool cursorOnThisSurface)
    {
        SD.Size clientSize = monitorBounds.Size;
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
            SelectorChrome.DrawCrosshair(g, clientSize, ToLocal(_currentScreen, monitorBounds), crimson);

            if (_measured is SD.Rectangle measured)
                PaintLiveMeasurement(g, monitorBounds, measured, crimson);

            if (_magnifierOn)
            {
                FastSelectorLoupeRenderer.Draw(
                    g, clientSize, _vs, ToLocal(_currentScreen, monitorBounds), _currentScreen, _frozen);
            }

            PaintHintStrip(g, monitorBounds);
        }
    }

    private void PaintLiveMeasurement(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle run, SD.Color crimson)
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
                label: null, crimson);
        }
        if (!locked || !width)
        {
            SelectorChrome.DrawDimension(g, clientSize,
                ToLocal(new SD.Point(column, run.Top), monitorBounds),
                ToLocal(new SD.Point(column, run.Bottom - 1), monitorBounds),
                label: null, crimson);
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
            SD.Color? outline = _pillHover > 0.01
                ? SD.Color.FromArgb((int)Math.Round(255 * Math.Clamp(_pillHover, 0, 1)), crimson)
                : null;
            SelectorChrome.DrawLabel(g, clientSize, MeasureLiveLabel(run), local.X, local.Y,
                fullyRounded: true, outline: outline);
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
        SelectorChrome.DrawLabel(g, monitorBounds.Size, label, at.X, at.Y, fullyRounded: true);
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

        SD.Size pill = SelectorChrome.MeasureLabel(label);
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

    private void PaintHolds(SD.Graphics g, SD.Rectangle monitorBounds, SD.Color crimson)
    {
        foreach (MeasureHold hold in _measure.Holds)
        {
            SD.Rectangle bounds = hold.Bounds;
            if (!bounds.IntersectsWith(monitorBounds))
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
                    MeasureFormat.SpanLabel(bounds.Width, dpi), crimson);
            }
            else
            {
                int column = Math.Clamp(hold.At.X, bounds.Left, bounds.Right - 1);
                SelectorChrome.DrawDimension(g, monitorBounds.Size,
                    ToLocal(new SD.Point(column, bounds.Top), monitorBounds),
                    ToLocal(new SD.Point(column, bounds.Bottom - 1), monitorBounds),
                    MeasureFormat.SpanLabel(bounds.Height, dpi), crimson);
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
        int dpi = MonitorDpiAt(monitorBounds.Location);
        SD.Point cursor = _currentScreen;

        foreach (var (from, to) in MeasureSession.Pairs(_measure.GuidesX))
        {
            int y = Math.Clamp(cursor.Y, monitorBounds.Top + 24, monitorBounds.Bottom - 24);
            SelectorChrome.DrawDimension(g, monitorBounds.Size,
                ToLocal(new SD.Point(from, y), monitorBounds),
                ToLocal(new SD.Point(to, y), monitorBounds),
                MeasureFormat.SpanLabel(to - from, dpi), guideBlue, serifs: false);
        }

        foreach (var (from, to) in MeasureSession.Pairs(_measure.GuidesY))
        {
            int x = Math.Clamp(cursor.X, monitorBounds.Left + 24, monitorBounds.Right - 24);
            SelectorChrome.DrawDimension(g, monitorBounds.Size,
                ToLocal(new SD.Point(x, from), monitorBounds),
                ToLocal(new SD.Point(x, to), monitorBounds),
                MeasureFormat.SpanLabel(to - from, dpi), guideBlue, serifs: false);
        }
    }

    private const string HintSeparator = "  ·  ";

    /// <summary>Everything this mode can do, in reading order. Alt-toggles-magnifier is
    /// undiscoverable in the ordinary selector and always has been; measurement has nine
    /// gestures and cannot afford the same silence.</summary>
    private string[] HintStripItems() => new[]
    {
        $"Tolerance {_measure.Tolerance}",
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
        if (_frozen is null)
        {
            return _freezeFailed
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
    internal string HintStripTextFor(int monitorWidth)
    {
        if (_frozen is null)
            return HintStripText();

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
            if (SelectorChrome.MeasureLabel(wider).Width <= monitorWidth)
            {
                line = wider;
                continue;
            }
            lines.Add(line);
            line = item;
        }
        lines.Add(line);

        return lines.Count <= MaxLines
            ? string.Join("\n", lines)
            : $"Tolerance {_measure.Tolerance}  ·  Right-click for more";
    }

    /// <summary>Where the strip sits on one pane, in that pane's local px. Centred along the
    /// bottom, and lifted clear of the loupe when the two want the same ground: the loupe is
    /// forced on in this mode because it is part of the tool, so chrome does not get to paint
    /// over its coordinates and its hex swatch.</summary>
    private SD.Rectangle HintStripRect(SD.Rectangle monitorBounds)
    {
        SD.Size pill = SelectorChrome.MeasureLabel(HintStripTextFor(monitorBounds.Width));
        int gap = (int)Math.Round(
            HintStripBottomGap * WinShot.Recording.RecordingMonitorDpi.ScaleFor(monitorBounds));
        int left = Math.Max(0, (monitorBounds.Width - pill.Width) / 2);
        int top = monitorBounds.Height - gap - pill.Height;

        if (_magnifierOn && monitorBounds.Contains(_currentScreen))
        {
            SD.Rectangle loupe = FastSelectorLoupeRenderer.OccupiedRect(
                monitorBounds.Size, _vs, ToLocal(_currentScreen, monitorBounds), _currentScreen);
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
        SelectorChrome.DrawLabel(g, monitorBounds.Size, HintStripTextFor(monitorBounds.Width),
            strip.Left, strip.Top + (int)Math.Round(8 * (1 - _hintAmount)),
            fullyRounded: true, amount: _hintAmount);
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
