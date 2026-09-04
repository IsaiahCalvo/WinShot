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
/// reads as imprecise. The only motion here is the hint strip's entrance, which is chrome.
/// </summary>
public sealed partial class FastRegionSelectorDialog
{
    private readonly MeasureSession _measure = new();

    /// <summary>The live run under the cursor, in screen px. Null when there is nothing to
    /// read from (no frozen snapshot, or the cursor is off the snapshot).</summary>
    private SD.Rectangle? _measured;

    /// <summary>The two colours the live run is made of, cached against the run it was
    /// sampled for so a stationary cursor does not re-histogram on every follow tick.</summary>
    private (SD.Color Background, SD.Color Foreground)? _contrast;
    private SD.Rectangle _contrastFor;

    /// <summary>The guide being dragged: which axis, and where it currently sits.</summary>
    private (bool Vertical, int At)? _draggingGuide;

    private IDisposable? _hintTween;
    private double _hintAmount;

    /// <summary>The one live right-click menu. Kept in a field rather than disposed from its
    /// own Closed handler: WinForms' Modal menu filter still touches a closing drop-down on
    /// the next few messages, and disposing inside that window takes the process down.</summary>
    private WF.ContextMenuStrip? _measureMenu;

    /// <summary>How close the cursor must come to a guide to grab it.</summary>
    private const int GuideGrabPx = 4;

    private const int HintStripBottomGap = 28;

    /// <summary>Perpendicular gap between the measured line and its pill; mirrors the same
    /// constant in <see cref="SelectorChrome.DrawDimension"/>.</summary>
    private const int DimensionPillGap = 6;

    internal MeasureSession MeasureState => _measure;

    /// <summary>The live measured run, in screen px — for tests, which cannot see a screen.</summary>
    internal SD.Rectangle? MeasuredScreenRect => _measured;

    internal double HintAmount => _hintAmount;

    /// <summary>
    /// Render seam. Puts the selector into Measure mode over a supplied snapshot without
    /// opening a window, so the measure chrome can be painted into a bitmap and pixel-scanned
    /// on the hidden test desktop — the plan's rule is that render assertions are scanned, not
    /// eyeballed. Nothing in the app calls this; the real path is <see cref="ShowAsync"/>.
    /// </summary>
    internal void EnterMeasureForRender(SD.Bitmap frozen, SD.Rectangle virtualScreen, SD.Point cursor)
    {
        _mode = SelectorMode.Measure;
        _vs = virtualScreen;
        _monitorBounds = virtualScreen;
        Bounds = virtualScreen;
        _frozen = frozen;
        _magnifierOn = false;
        ResetMeasure();
        _hintAmount = 1;
        MoveMeasureCursorForRender(cursor);
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
        _contrast = null;
        _contrastFor = SD.Rectangle.Empty;
        _draggingGuide = null;
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

        var (run, _) = ReadPixels(screen, EdgeSnap.DefaultTolerance, wantColors: false);
        if (run is not SD.Rectangle rect || rect.Width < 4 || rect.Height < 4)
            return null;
        return rect == MonitorBoundsAt(screen) ? null : rect;
    }

    // ----------------------------------------------------------- input

    private void HandleMeasureMouseMove()
    {
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
            return;
        }

        SD.Rectangle? before = _measured;
        var (run, colors) = ReadPixels(_currentScreen, _measure.Tolerance, wantColors: true);
        _measured = run;
        if (run is SD.Rectangle rect)
        {
            _contrast = colors;
            _contrastFor = rect;
        }
        else
        {
            _contrast = null;
        }

        // The crosshair, the pill and the contrast readout all move with the cursor, and the
        // run itself can jump anywhere: repaint the union of where it was and where it is,
        // plus the follow bands, rather than the whole (possibly 4K) monitor.
        InvalidateFollowRegion(_lastFollowScreen, _currentScreen);
        _lastFollowScreen = _currentScreen;
        if (before is SD.Rectangle old)
            InvalidateMeasureRegion(old);
        if (_measured is SD.Rectangle now && now != before)
            InvalidateMeasureRegion(now);
    }

    /// <summary>The ground one measurement covers: its own rect plus room above it for the
    /// pill and the contrast readout.</summary>
    private void InvalidateMeasureRegion(SD.Rectangle screenRect)
    {
        var region = screenRect;
        region.Inflate(8, 8);
        region = SD.Rectangle.FromLTRB(region.Left, region.Top - 80, region.Right, region.Bottom + 8);
        InvalidateScreenRect(region);
    }

    /// <summary>Returns true when the key was a measurement gesture and the ordinary
    /// selector handling must not also run.</summary>
    private bool HandleMeasureKeyDown(WF.KeyEventArgs e)
    {
        bool shift = (e.Modifiers & WF.Keys.Shift) == WF.Keys.Shift;
        bool ctrl = (e.Modifiers & WF.Keys.Control) == WF.Keys.Control;

        switch (e.KeyCode)
        {
            case WF.Keys.H when shift:
                _measure.AddGuideY(SnapCoordinate(_currentScreen.Y, _snapEdgesY));
                Handled(e);
                return true;

            case WF.Keys.V when shift:
                _measure.AddGuideX(SnapCoordinate(_currentScreen.X, _snapEdgesX));
                Handled(e);
                return true;

            case WF.Keys.H when _measured is SD.Rectangle wide:
                _measure.Hold(wide, horizontal: true, _currentScreen);
                Handled(e);
                return true;

            case WF.Keys.V when _measured is SD.Rectangle tall:
                _measure.Hold(tall, horizontal: false, _currentScreen);
                Handled(e);
                return true;

            case WF.Keys.Back:
                if (_measure.RemoveLastHold())
                    Handled(e);
                return true;

            case WF.Keys.C when ctrl && shift:
                CopyPixelColor();
                Handled(e);
                return true;

            case WF.Keys.C when ctrl:
                CopyMeasurement();
                Handled(e);
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
                CaptureMeasured(target);
                return true;

            case WF.Keys.ShiftKey:
                // Shift locks the readout to one axis; the paint reads ModifierKeys, so all
                // this has to do is ask for a repaint.
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

        // A guide under the pointer is a thing to drag, and it wins over everything else.
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
            return;

        // Click the pill and you capture the thing you were measuring; click the thing and
        // you copy its dimensions. One gesture, two meanings, decided by where it lands.
        if (MeasurePillScreenRect() is SD.Rectangle pill && pill.Contains(screen))
            CaptureMeasured(run);
        else
            CopyMeasurement();
    }

    private void HandleMeasureWheel(WF.MouseEventArgs e)
    {
        if (!_measure.AdjustTolerance(e.Delta))
            return;

        PersistTolerance();
        var (run, colors) = ReadPixels(_currentScreen, _measure.Tolerance, wantColors: true);
        _measured = run;
        _contrast = run is null ? null : colors;
        if (run is SD.Rectangle rect)
            _contrastFor = rect;
        InvalidateAllSurfaces();
    }

    private WF.Cursor MeasureCursor()
    {
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

    private void CopyMeasurement()
    {
        string text = _measure.CopyText(_measured, MonitorDpiAt(_currentScreen));
        if (text.Length == 0)
            return;
        if (TrySetClipboardText(text))
            App.Toast("Measure", _measure.Holds.Count > 1 ? "Measurements copied." : "Dimensions copied.");
    }

    private void CopyPixelColor()
    {
        if (_frozen is null)
            return;

        SD.Color color;
        try
        {
            using var snap = EdgeSnap.Lock(_frozen);
            color = snap.ColorAt(_currentScreen.X - _vs.X, _currentScreen.Y - _vs.Y);
        }
        catch (Exception ex)
        {
            Log.Error("Reading the pixel colour under the cursor failed", ex);
            return;
        }

        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (TrySetClipboardText(hex))
            App.Toast("Measure", $"Copied {hex}.");
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

    /// <summary>Frees the previous menu, which by now is several message loops closed.</summary>
    internal void DisposeMeasureMenu()
    {
        CloseMeasureMenu();
        _measureMenu?.Dispose();
        _measureMenu = null;
    }

    private void ShowMeasureMenu(SD.Point screen)
    {
        DisposeMeasureMenu();
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

        bool measuring = _measured is not null;
        Row("Hold width", "H", () => { if (_measured is SD.Rectangle r) _measure.Hold(r, true, _currentScreen); }, measuring);
        Row("Hold height", "V", () => { if (_measured is SD.Rectangle r) _measure.Hold(r, false, _currentScreen); }, measuring);
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Add horizontal guide", "Shift+H", () => _measure.AddGuideY(SnapCoordinate(_currentScreen.Y, _snapEdgesY)));
        Row("Add vertical guide", "Shift+V", () => _measure.AddGuideX(SnapCoordinate(_currentScreen.X, _snapEdgesX)));
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Copy measurement", "Ctrl+C", CopyMeasurement, measuring || _measure.Holds.Count > 0);
        Row("Copy pixel colour", "Ctrl+Shift+C", CopyPixelColor);
        Row(_measure.UseApca ? "Show WCAG contrast" : "Show APCA contrast", "Alt+click",
            () => _measure.ToggleContrastScale());
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Remove last hold", "Backspace", () => _measure.RemoveLastHold(), _measure.Holds.Count > 0);
        Row("Clear measurements", "Esc", () => _measure.ClearOnEscape(), _measure.HasAnything);
        menu.Items.Add(new WF.ToolStripSeparator());
        Row("Capture this object", "Enter", () => { if (_measured is SD.Rectangle r) CaptureMeasured(r); }, measuring);
        Row("Exit measure mode", "Esc", () => Complete(WF.DialogResult.Cancel));

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
        SD.Rectangle monitor = MonitorBoundsAt(_currentScreen);
        InvalidateScreenRect(new SD.Rectangle(
            monitor.Left, monitor.Bottom - HintStripBottomGap - 48, monitor.Width, HintStripBottomGap + 48));
    }

    // ----------------------------------------------------------- painting

    private int MonitorDpiAt(SD.Point screen) =>
        (int)Math.Round(96 * WinShot.Recording.RecordingMonitorDpi.ScaleFor(MonitorBoundsAt(screen)));

    private static bool AxisLocked => (WF.Control.ModifierKeys & WF.Keys.Shift) == WF.Keys.Shift;

    /// <summary>Which single axis Shift locks to: the longer span, because that is the one
    /// the eye was already on.</summary>
    private static bool LockedToWidth(SD.Rectangle run) => run.Width >= run.Height;

    private string MeasureLiveLabel(SD.Rectangle run)
    {
        int dpi = MonitorDpiAt(_currentScreen);
        if (!AxisLocked)
            return MeasureFormat.CrossLabel(run.Size, dpi);
        return MeasureFormat.SpanLabel(LockedToWidth(run) ? run.Width : run.Height, dpi);
    }

    /// <summary>
    /// Where the live pill lands, in screen px. Paint and hit-test both come through here —
    /// clicking the pill means something different from clicking the object, so the two must
    /// agree on the pixel.
    /// </summary>
    private SD.Rectangle? MeasurePillScreenRect()
    {
        if (_measured is not SD.Rectangle run)
            return null;

        SD.Size pill = SelectorChrome.MeasureLabel(MeasureLiveLabel(run));
        SD.Rectangle monitor = MonitorBoundsAt(_currentScreen);
        int row = Math.Clamp(_currentScreen.Y, run.Top, run.Bottom - 1);
        int centre = (run.Left + run.Right - 1) / 2;
        int left = Math.Clamp(centre - (pill.Width / 2), monitor.Left, Math.Max(monitor.Left, monitor.Right - pill.Width));
        // Above the line, flipped below when there is no room — clamping instead would park
        // the pill on top of the very thing being measured.
        int above = row - pill.Height - DimensionPillGap;
        int top = above >= monitor.Top ? above : row + DimensionPillGap;
        top = Math.Clamp(top, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - pill.Height));
        return new SD.Rectangle(left, top, pill.Width, pill.Height);
    }

    private void PaintMeasureSurface(SD.Graphics g, SD.Rectangle monitorBounds, bool cursorOnThisSurface)
    {
        SD.Size clientSize = monitorBounds.Size;
        PaintFrozenGround(g, monitorBounds);

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

    /// <summary>The dimmed frozen slice, or a plain dim when the snapshot never arrived.</summary>
    private void PaintFrozenGround(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        var dimmed = GetDimmedBackground(monitorBounds);
        if (dimmed is not null)
        {
            var previous = g.CompositingMode;
            g.CompositingMode = SD.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(dimmed, new SD.Rectangle(0, 0, monitorBounds.Width, monitorBounds.Height),
                0, 0, dimmed.Width, dimmed.Height, SD.GraphicsUnit.Pixel);
            g.CompositingMode = previous;
            return;
        }

        using var dim = new SD.SolidBrush(SD.Color.FromArgb(115, 0, 0, 0));
        g.FillRectangle(dim, 0, 0, monitorBounds.Width, monitorBounds.Height);
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
        if (MeasurePillScreenRect() is SD.Rectangle pill)
        {
            SD.Point local = ToLocal(pill.Location, monitorBounds);
            SelectorChrome.DrawLabel(g, clientSize, MeasureLiveLabel(run), local.X, local.Y, fullyRounded: true);
        }

        PaintContrastReadout(g, monitorBounds, run);
    }

    /// <summary>The contrast of the two colours the measured region is made of, above it.
    /// Alt+click flips between WCAG's ratio and APCA's Lc — they answer different questions
    /// and the label always says which one is showing.</summary>
    private void PaintContrastReadout(SD.Graphics g, SD.Rectangle monitorBounds, SD.Rectangle run)
    {
        if (_contrast is not (SD.Color background, SD.Color foreground) || _contrastFor != run)
            return;
        if (background.ToArgb() == foreground.ToArgb())
            return; // a flat region has no contrast to report, and "1.0 : 1" is noise

        string label = ColorMath.ContrastLabel(foreground, background, _measure.UseApca);
        SD.Size pill = SelectorChrome.MeasureLabel(label);
        // Above the region, tucked just inside its top when there is no room above — the
        // clamp alone would pin it to the screen corner, a long way from what it describes.
        int above = run.Top - pill.Height - 8;
        int top = above >= monitorBounds.Top ? above : run.Top + 8;
        SD.Point at = ToLocal(new SD.Point(run.Left, top), monitorBounds);
        SelectorChrome.DrawLabel(g, monitorBounds.Size, label, at.X, at.Y, fullyRounded: true);
    }

    private void PaintHolds(SD.Graphics g, SD.Rectangle monitorBounds, SD.Color crimson)
    {
        int dpi = MonitorDpiAt(_currentScreen);
        foreach (MeasureHold hold in _measure.Holds)
        {
            SD.Rectangle bounds = hold.Bounds;
            if (!bounds.IntersectsWith(monitorBounds))
                continue;

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
    /// cursor's end of the span — you read the number where you are looking.</summary>
    private void PaintGuidePairs(SD.Graphics g, SD.Rectangle monitorBounds, SD.Color guideBlue)
    {
        int dpi = MonitorDpiAt(_currentScreen);
        SD.Point cursor = _currentScreen;

        foreach (var (from, to) in MeasureSession.Pairs(_measure.GuidesX))
        {
            int y = Math.Clamp(cursor.Y, monitorBounds.Top + 24, monitorBounds.Bottom - 24);
            SelectorChrome.DrawDimension(g, monitorBounds.Size,
                ToLocal(new SD.Point(from, y), monitorBounds),
                ToLocal(new SD.Point(to, y), monitorBounds),
                MeasureFormat.SpanLabel(to - from, dpi), guideBlue);
        }

        foreach (var (from, to) in MeasureSession.Pairs(_measure.GuidesY))
        {
            int x = Math.Clamp(cursor.X, monitorBounds.Left + 24, monitorBounds.Right - 24);
            SelectorChrome.DrawDimension(g, monitorBounds.Size,
                ToLocal(new SD.Point(x, from), monitorBounds),
                ToLocal(new SD.Point(x, to), monitorBounds),
                MeasureFormat.SpanLabel(to - from, dpi), guideBlue);
        }
    }

    /// <summary>
    /// Everything this mode can do, on one line along the bottom. Alt-toggles-magnifier is
    /// undiscoverable in the ordinary selector and always has been; measurement has six
    /// gestures and cannot afford the same silence.
    /// </summary>
    internal string HintStripText() =>
        $"Tolerance {_measure.Tolerance}  ·  H / V hold  ·  Shift+H / Shift+V guide  ·  " +
        "Ctrl+C copy  ·  Alt+click contrast  ·  Backspace undo  ·  Right-click for more  ·  Esc";

    /// <summary>
    /// The strip fades and rises 8 px into place. GDI+ has no global alpha for a text draw,
    /// so the fade is applied to the two colours instead of to a layer — which is why this
    /// draws its own pill rather than calling DrawLabel.
    /// </summary>
    private void PaintHintStrip(SD.Graphics g, SD.Rectangle monitorBounds)
    {
        if (_hintAmount <= 0.01)
            return;

        string text = HintStripText();
        SD.Size pill = SelectorChrome.MeasureLabel(text);
        int left = (monitorBounds.Width - pill.Width) / 2;
        int top = monitorBounds.Height - HintStripBottomGap - pill.Height + (int)Math.Round(8 * (1 - _hintAmount));
        var bounds = new SD.Rectangle(left, top, pill.Width, pill.Height);

        int groundAlpha = (int)Math.Round(235 * _hintAmount);
        int textAlpha = (int)Math.Round(255 * _hintAmount);

        var previous = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = GdiPaths.RoundedRect(bounds, Math.Max(2, bounds.Height / 2)))
        using (var ground = new SD.SolidBrush(SD.Color.FromArgb(groundAlpha, 0x1C, 0x1C, 0x1E)))
            g.FillPath(ground, path);
        g.SmoothingMode = previous;

        using var font = ThemePalette.UiFont(ThemePalette.TextBodyPt, SD.FontStyle.Bold);
        WF.TextRenderer.DrawText(g, text, font, bounds,
            SD.Color.FromArgb(textAlpha, ThemePalette.TextPrimary),
            WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.NoPadding);
    }
}
