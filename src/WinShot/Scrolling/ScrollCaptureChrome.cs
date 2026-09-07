using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Scrolling;

/// <summary>
/// Marks a window so it stays visible on screen but is skipped by screen capture
/// (WGC / Desktop Duplication). Essential for the scrolling chrome: the dim overlay and
/// controls must NOT end up in the frames the capture loop stitches.
/// </summary>
internal static class CaptureExclusion
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint LWA_ALPHA = 0x2;
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    public static void Apply(IntPtr hwnd)
    {
        try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
        catch { /* pre-2004 Windows: no exclusion available; non-fatal */ }
    }

    /// <summary>
    /// Display affinity covers WGC/Desktop Duplication but is not guaranteed for plain
    /// BitBlt — which is exactly what the scrolling frame grab uses. The grab omits
    /// CAPTUREBLT, so LAYERED windows are skipped by construction: marking the chrome
    /// layered (at full opacity) makes its exclusion from the stitch deterministic.
    /// Call from OnHandleCreated on a form whose CreateParams include WS_EX_LAYERED.
    /// </summary>
    public static void ApplyLayeredOpaque(IntPtr hwnd)
    {
        Apply(hwnd);
        SetLayeredAlpha(hwnd, 255);
    }

    /// <summary>
    /// Sets a layered window's constant alpha directly (255 = opaque). The selectors use
    /// this instead of Form.Opacity for the live→frozen swap: Form.Opacity's layered-style
    /// bookkeeping recreates the window handle, which costs 150-500 ms on a full-screen
    /// TopMost surface and would hitch the open selector.
    /// </summary>
    public static void SetLayeredAlpha(IntPtr hwnd, byte alpha)
    {
        try { SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA); }
        catch { /* worst case the window keeps its current alpha */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
}

/// <summary>
/// The scrolling chrome's show/hide ramp. All three surfaces are layered windows, so their
/// alpha is set directly rather than through Form.Opacity -- which recreates the handle, and
/// on a full-monitor TopMost surface costs more than the ramp is worth. The exit runs the
/// mirror ease so leaving reads as arriving played backwards.
/// </summary>
internal static class ScrollChromeFade
{
    /// <summary>From the vocabulary: the dim, the controls bar and the preview panel are
    /// three windows arriving on screen, which is what <see cref="Motion.EnterDurationMs"/>
    /// names -- QuickMs is for hover fills and other same-place tint changes.</summary>
    public const int DurationMs = Motion.EnterDurationMs;

    public static IDisposable In(Action<byte> apply)
    {
        apply(0);
        return Motion.Run(DurationMs, Motion.EaseOutCubic, p => apply(Alpha(p)));
    }

    /// <summary>
    /// Fades out and completes when it is gone. The cancel handle comes back too: the caller
    /// closes its form on a timeout race, and a discarded handle left an uncancellable tween
    /// holding Motion.Acquire() with its form already destroyed.
    /// </summary>
    public static Task Out(Action<byte> apply, out IDisposable handle)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handle = Motion.Run(DurationMs, Motion.EaseInCubic, p => apply(Alpha(1d - p)),
            () => done.TrySetResult());
        return done.Task;
    }

    private static byte Alpha(double amount) => (byte)Math.Clamp(Math.Round(amount * 255d), 0d, 255d);
}

/// <summary>
/// Full-monitor dim with the capture region punched out as a bright, accent-framed hole,
/// so during a scrolling capture the user sees exactly what's being captured while still
/// scrolling the live content underneath. Click-through (WS_EX_TRANSPARENT) so all input
/// passes to the content, and excluded from capture so it never taints the stitched frames.
/// </summary>
public sealed class ScrollDimOverlay : WF.Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    private readonly SD.Rectangle _monitor;
    private readonly SD.Rectangle _holeLocal;
    private SD.Bitmap? _surface;
    private IDisposable? _fade;

    public ScrollDimOverlay(SD.Rectangle regionScreen)
    {
        _monitor = WF.Screen.FromRectangle(regionScreen).Bounds;
        var hole = SD.Rectangle.Intersect(regionScreen, _monitor);
        _holeLocal = new SD.Rectangle(hole.X - _monitor.X, hole.Y - _monitor.Y, hole.Width, hole.Height);

        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        Bounds = _monitor;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExclusion.Apply(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            RenderLayered();
            _fade = ScrollChromeFade.In(SetAlpha);
        }
        catch (Exception ex) { Log.Error("Scroll dim overlay render failed (non-fatal)", ex); }
    }

    /// <summary>Fades the dim out and completes once it is gone; the caller then closes.</summary>
    public Task FadeOutAsync()
    {
        if (IsDisposed || _surface is null) return Task.CompletedTask;
        _fade?.Dispose();
        return ScrollChromeFade.Out(SetAlpha, out _fade);
    }

    private void SetAlpha(byte alpha)
    {
        if (IsDisposed || _surface is null || !IsHandleCreated) return;
        PushLayered(_surface, alpha);
    }

    protected override void OnFormClosed(WF.FormClosedEventArgs e)
    {
        _fade?.Dispose();
        _fade = null;
        _surface?.Dispose();
        _surface = null;
        base.OnFormClosed(e);
    }

    private void RenderLayered()
    {
        int w = Math.Max(1, _monitor.Width), h = Math.Max(1, _monitor.Height);
        var bmp = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;
            using (var dim = new SD.SolidBrush(SD.Color.FromArgb(110, 0, 0, 0)))
                g.FillRectangle(dim, 0, 0, w, h);

            if (_holeLocal.Width > 0 && _holeLocal.Height > 0)
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SD.SolidBrush(SD.Color.FromArgb(0, 0, 0, 0)))
                    g.FillRectangle(clear, _holeLocal);

                g.CompositingMode = CompositingMode.SourceOver;
                using var pen = new SD.Pen(SD.Color.FromArgb(255, ThemePalette.Accent), 2f);
                g.DrawRectangle(pen, _holeLocal.X - 2, _holeLocal.Y - 2, _holeLocal.Width + 3, _holeLocal.Height + 3);
            }
        }
        _surface?.Dispose();
        _surface = bmp;
        PushLayered(bmp, 0);
    }

    private void PushLayered(SD.Bitmap bmp, byte constantAlpha)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(SD.Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = _monitor.X, y = _monitor.Y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = constantAlpha,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
}

/// <summary>
/// CleanShot-style controls bar shown next to the capture region. Before capture:
/// Start Capture / Auto-Scroll / Cancel. During: live status (or a persistent amber hint),
/// the Auto-Scroll toggle, Done and Cancel. Placed just below the region; flips above when
/// the region runs to the bottom of the screen, and tucks inside the bottom of the region
/// when it spans the full screen height. Never activates (wheel keeps reaching the content)
/// and is excluded from capture. Done keeps the stitched result; Cancel and Esc discard it.
/// </summary>
public sealed class ScrollControlsBar : WF.Form
{
    public event Action? CancelRequested;
    public event Action? DoneRequested;
    public event Action? StartRequested;
    /// <summary>Fires with the new autopilot state; clicking Auto-Scroll before Start also starts.</summary>
    public event Action<bool>? AutoScrollToggled;

    private readonly WF.Label _status;
    private readonly WF.Button _start;
    private readonly WF.Button _auto;
    private readonly WF.Button _done;
    private readonly SD.Rectangle _region;
    private bool _started;
    private bool _autoOn;
    private readonly WinShot.Capture.RectangleTween _reveal = new();
    private IDisposable? _revealTween;
    private IDisposable? _fade;
    private ScrollHint _hint = ScrollHint.None; // a persistent hint overrides live status text

    // Point-based fonts render at the monitor's DPI, so the fixed-pixel layout has to be
    // scaled for the target monitor or the status label and buttons clip on a 125%/150%
    // display. AutoScaleMode.Dpi cannot do this: it only reacts to DPI *changes*, so on
    // the monitor a window is born on it never scales at all. Same treatment as every
    // other WinForms surface in the app (FastRecordingControlBar, the selectors, ...).
    private readonly double _scale;

    private int S(int logical) => WinShot.Recording.RecordingMonitorDpi.Px(logical, _scale);

    public ScrollControlsBar(SD.Rectangle regionScreen)
    {
        SD.Rectangle targetScreen = WF.Screen.FromRectangle(regionScreen).Bounds;
        _scale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(targetScreen);
        // Born ON the target monitor: a window created at (0,0) on the primary and moved
        // later gets WM_DPICHANGED on the move and rescales on top of the pre-applied
        // _scale, which double-scales the bar on a mixed-DPI setup.
        Location = targetScreen.Location;
        AutoScaleMode = WF.AutoScaleMode.None;
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        BackColor = ThemePalette.Elevated;
        Padding = new WF.Padding(S(12), S(8), S(12), S(8));

        // Fixed width so the live status text ("12 frames — 4000px") never grows the
        // bar mid-capture (which would otherwise leave the rounded region stale and clip Done).
        _status = new WF.Label
        {
            Name = "ScrollCaptureStatus",
            AutoSize = false,
            Size = new SD.Size(S(250), S(24)),
            AutoEllipsis = true,
            ForeColor = ThemePalette.TextSecondary,
            Font = ThemePalette.UiFont(ThemePalette.TextBodyPt),
            Text = "Scroll capture ready",
            // Before capture the bar is a single Start Capture capsule: a status line
            // reading "ready" and a toggle for a run that hasn't begun are both answers to
            // questions nobody has asked yet. Start() reveals them.
            Visible = false,
            TextAlign = SD.ContentAlignment.MiddleLeft,
            Anchor = WF.AnchorStyles.None,
            AccessibleName = "Scrolling capture status",
            AccessibleDescription = "Live capture progress and recovery guidance.",
            AccessibleRole = WF.AccessibleRole.StaticText,
        };

        var cancel = MakeButton("Cancel", "ScrollCaptureCancel", "Cancel scrolling capture",
            "Discard the capture. Keyboard shortcut: Alt+X or Escape.",
            ThemePalette.SurfaceAlt, ThemePalette.TextPrimary);
        cancel.Click += (_, _) => PerformAction(ScrollChromeAction.Cancel);

        _auto = MakeButton("Auto-Scroll", "ScrollCaptureAuto", "Toggle automatic scrolling",
            "Start or pause automatic scrolling. Auto-scroll runs only while the pointer is over the region. Keyboard shortcut: Alt+A.",
            ThemePalette.SurfaceAlt, ThemePalette.TextPrimary);
        _auto.Visible = false; // revealed with the status line when capture starts
        _auto.Click += (_, _) => PerformAction(ScrollChromeAction.ToggleAuto);

        _start = MakeButton("Start Capture", "ScrollCaptureStart", "Start scrolling capture",
            "Begin capturing without moving focus from the captured app. Keyboard shortcut: Alt+S.",
            ThemePalette.Accent, SD.Color.White);
        _start.Click += (_, _) => PerformAction(ScrollChromeAction.Start);

        _done = MakeButton("Done", "ScrollCaptureDone", "Finish scrolling capture",
            "Keep the captured result. Keyboard shortcut: Alt+D.",
            ThemePalette.Accent, SD.Color.White);
        _done.Visible = false;
        _done.Click += (_, _) => PerformAction(ScrollChromeAction.Done);

        var table = new WF.TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            RowCount = 1,
            Dock = WF.DockStyle.Fill,
            BackColor = SD.Color.Transparent,
        };
        table.Controls.Add(_status, 0, 0);
        table.Controls.Add(_auto, 1, 0);
        table.Controls.Add(_start, 2, 0);
        table.Controls.Add(_done, 3, 0);
        table.Controls.Add(cancel, 4, 0);

        Controls.Add(table);
        AutoSize = true;
        AutoSizeMode = WF.AutoSizeMode.GrowAndShrink;

        _region = regionScreen;
        Shown += (_, _) =>
        {
            ApplyRoundedRegion(Width, Height, S(12));
            PositionNear(_region);
        };
        // Re-round AND re-center when the bar resizes (Start swaps to Done), so it never
        // clips the content or drifts off-center.
        SizeChanged += (_, _) =>
        {
            ApplyRoundedRegion(Width, Height, S(12));
            if (!_reveal.IsActive) PositionNear(_region); // the glide owns the position while it runs
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= CaptureExclusion.WS_EX_LAYERED | CaptureExclusion.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExclusion.ApplyLayeredOpaque(Handle);
        CaptureExclusion.SetLayeredAlpha(Handle, 0); // the fade in OnShown brings it up
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _fade = ScrollChromeFade.In(SetChromeAlpha);
    }

    /// <summary>Fades the bar out and completes once it is gone; the caller then closes.</summary>
    public Task FadeOutAsync()
    {
        if (IsDisposed || !IsHandleCreated) return Task.CompletedTask;
        _fade?.Dispose();
        return ScrollChromeFade.Out(SetChromeAlpha, out _fade);
    }

    private void SetChromeAlpha(byte alpha)
    {
        if (IsDisposed || !IsHandleCreated) return;
        CaptureExclusion.SetLayeredAlpha(Handle, alpha);
    }

    private void Start()
    {
        if (_started) return;
        _started = true;
        _start.Visible = false;
        _done.Visible = true;
        RevealCaptureControls();
        StartRequested?.Invoke();
    }

    /// <summary>
    /// Grows the capsule into the full capture bar. The controls appear at their final size
    /// and the window glides from the capsule's rect to the bar's, re-centred on the region,
    /// so the bar reads as expanding rather than being replaced.
    /// </summary>
    private void RevealCaptureControls()
    {
        if (IsDisposed) return;
        if (!IsHandleCreated)
        {
            _status.Visible = true;
            _auto.Visible = true;
            return;
        }

        SD.Rectangle from = Bounds;
        AutoSize = false; // the glide sets Size itself; AutoSize would snap it back
        _status.Visible = true;
        _auto.Visible = true;
        PerformLayout();
        SD.Size grown = PreferredSize;
        SD.Rectangle to = new(from.X + ((from.Width - grown.Width) / 2), from.Y, grown.Width, grown.Height);

        _revealTween?.Dispose();
        _reveal.Retarget(from, to);
        _revealTween = Motion.Run(Motion.SwapMs, Motion.EaseOutCubic,
            p =>
            {
                if (IsDisposed) return;
                _reveal.Update((float)p);
                Bounds = _reveal.Current;
            },
            () =>
            {
                _reveal.Stop();
                if (IsDisposed) return;
                AutoSize = true;
                PositionNear(_region);
            });
    }

    private void ToggleAuto()
    {
        _autoOn = !_autoOn;
        _auto.Text = _autoOn ? "Pause Auto" : "Auto-Scroll";
        if (_autoOn && !_started)
            Start();
        AutoScrollToggled?.Invoke(_autoOn);
    }

    /// <summary>Runs a scoped keyboard command without activating the controls window.</summary>
    internal void PerformAction(ScrollChromeAction action)
    {
        switch (action)
        {
            case ScrollChromeAction.Start:
                Start();
                break;
            case ScrollChromeAction.ToggleAuto:
                ToggleAuto();
                break;
            case ScrollChromeAction.Done when _started:
                DoneRequested?.Invoke();
                break;
            case ScrollChromeAction.Cancel:
                CancelRequested?.Invoke();
                break;
        }
    }

    /// <summary>Programmatic start for the scroll-region automation command.</summary>
    public void StartAutomatically(bool auto)
    {
        if (auto && !_autoOn) ToggleAuto();
        else Start();
    }

    public void SetStatus(string text)
    {
        if (IsDisposed || _hint != ScrollHint.None) return; // hints take precedence
        _status.ForeColor = ThemePalette.TextSecondary;
        _status.Text = text;
        Announce(text);
    }

    /// <summary>Shows a persistent hint that overrides the live status text until the service
    /// clears it (the next <see cref="SetStatus"/> repaints normal text once cleared). Amber
    /// for the nudges, body text for the pointer gate -- see ScrollHintText.ColorFor.</summary>
    public void SetHint(ScrollHint hint)
    {
        if (IsDisposed) return;
        _hint = hint;
        if (hint == ScrollHint.None) return;
        _status.ForeColor = ScrollHintText.ColorFor(hint);
        _status.Text = ScrollHintText.For(hint) ?? _status.Text;
        Announce(_status.Text);
    }

    private WF.Button MakeButton(string text, string name, string accessibleName,
        string accessibleDescription, SD.Color back, SD.Color fore)
    {
        var b = new WF.Button
        {
            Name = name,
            Text = text,
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            FlatStyle = WF.FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Padding = new WF.Padding(S(14), S(6), S(14), S(6)),
            Margin = new WF.Padding(S(8), 0, 0, 0),
            Cursor = WF.Cursors.Hand,
            Anchor = WF.AnchorStyles.None,
            Font = ThemePalette.UiFont(ThemePalette.TextBodyLgPt, SD.FontStyle.Bold),
            MinimumSize = new SD.Size(0, S(40)),
            AccessibleName = accessibleName,
            AccessibleDescription = accessibleDescription,
            AccessibleRole = WF.AccessibleRole.PushButton,
        };
        b.FlatAppearance.BorderSize = 0;
        // Both states come from the one function DarkButton paints with. Hover used to be a
        // colour hand-passed by the caller, so the neutral buttons here hovered to SurfaceHover
        // while every other neutral WinForms surface lifted +18 — the same button role wearing
        // two hover shades, decided in two places. And press used to BE the hover colour, so
        // holding down Start Capture, Done, Cancel or Auto-Scroll was pixel-identical to merely
        // pointing at it: four commit actions that acknowledged nothing.
        b.FlatAppearance.MouseOverBackColor = DarkControlPalette.StateFill(back, 1d, 0d);
        b.FlatAppearance.MouseDownBackColor = DarkControlPalette.StateFill(back, 1d, 1d);
        return b;
    }

    private void Announce(string text)
    {
        if (!IsHandleCreated || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _status.AccessibilityObject.RaiseAutomationNotification(
                WF.Automation.AutomationNotificationKind.Other,
                WF.Automation.AutomationNotificationProcessing.MostRecent,
                text);
        }
        catch
        {
            // UI Automation is optional; visible status remains the fallback.
        }
    }

    protected override void OnFormClosed(WF.FormClosedEventArgs e)
    {
        _revealTween?.Dispose();
        _revealTween = null;
        _fade?.Dispose();
        _fade = null;
        _reveal.Stop();
        base.OnFormClosed(e);
    }

    private void PositionNear(SD.Rectangle region)
    {
        var mon = WF.Screen.FromRectangle(region).Bounds;
        int gap = S(14), pad = S(8);
        int x = region.Left + (region.Width - Width) / 2;

        int below = region.Bottom + gap;
        int above = region.Top - Height - gap;
        int y;
        if (below + Height <= mon.Bottom - pad)
            y = below;                                  // room below the region
        else if (above >= mon.Top + pad)
            y = above;                                  // else above the region
        else
            y = region.Bottom - Height - gap;           // full-height region: tuck inside the bottom

        x = Math.Clamp(x, mon.Left + pad, Math.Max(mon.Left + pad, mon.Right - Width - pad));
        y = Math.Clamp(y, mon.Top + pad, Math.Max(mon.Top + pad, mon.Bottom - Height - pad));
        Location = new SD.Point(x, y);
    }

    private void ApplyRoundedRegion(int w, int h, int r)
    {
        if (w < r * 2 || h < r * 2) return;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, r, r, 180, 90);
        path.AddArc(w - r, 0, r, r, 270, 90);
        path.AddArc(w - r, h - r, r, r, 0, 90);
        path.AddArc(0, h - r, r, r, 90, 90);
        path.CloseFigure();
        Region = new SD.Region(path);
    }

}

internal enum ScrollChromeAction { Start, ToggleAuto, Done, Cancel }

internal static class ScrollKeyboardShortcuts
{
    public const string Start = "Alt+S";
    public const string ToggleAuto = "Alt+A";
    public const string Done = "Alt+D";
    public const string Cancel = "Alt+X";
}

/// <summary>User-facing text for the persistent capture hints, shared by the controls bar
/// and the preview panel (the user watches the bottom-right preview, so warnings must show
/// there too, not only on the bar).</summary>
internal static class ScrollHintText
{
    public static string? For(ScrollHint hint) => hint switch
    {
        ScrollHint.SlowDown => "⚠ Please slow down — scroll back up a little",
        ScrollHint.AlreadyCaptured => "Already captured — scroll down to continue",
        ScrollHint.MoveCursorHere => "Move the pointer over the region to keep scrolling",
        _ => null,
    };

    /// <summary>
    /// Amber is for the two nudges that mean something went wrong. MoveCursorHere is
    /// Auto-Scroll's ordinary paused state -- and the state the user is in the instant they
    /// press the button, since the controls bar sits outside the region -- so a warning
    /// colour there fires during normal use and stops meaning "warning".
    /// </summary>
    public static SD.Color ColorFor(ScrollHint hint) =>
        hint == ScrollHint.MoveCursorHere ? ThemePalette.TextSecondary : ThemePalette.Warn;
}

/// <summary>
/// Live preview panel pinned to the bottom-right of the region's monitor, showing the
/// stitched image growing as the capture proceeds (CleanShot shows the same). Excluded from
/// capture so it never appears in the stitch; never activates.
/// </summary>
public sealed class ScrollPreviewPanel : WF.Form
{
    private readonly WF.PictureBox _pic;
    private readonly WF.Label _caption;
    private bool _hintShowing;
    private IDisposable? _fade;

    // Same reason as ScrollControlsBar: AutoScaleMode.Dpi never fires on the monitor a
    // window is born on, so the panel laid out at 96-dpi metrics while its point-sized
    // caption rendered at the real DPI.
    private readonly double _scale;

    private int S(int logical) => WinShot.Recording.RecordingMonitorDpi.Px(logical, _scale);

    public ScrollPreviewPanel(SD.Rectangle regionScreen)
    {
        SD.Rectangle targetScreen = WF.Screen.FromRectangle(regionScreen).Bounds;
        _scale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(targetScreen);
        Location = targetScreen.Location;
        AutoScaleMode = WF.AutoScaleMode.None;
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        BackColor = ThemePalette.Elevated;
        Padding = new WF.Padding(S(8));
        Size = new SD.Size(S(220), S(300));

        _caption = new WF.Label
        {
            Name = "ScrollPreviewStatus",
            Dock = WF.DockStyle.Bottom,
            Height = S(32), // two lines, so the "slow down" warning fits
            ForeColor = ThemePalette.TextSecondary,
            Font = ThemePalette.UiFont(ThemePalette.TextCaptionPt),
            Text = "Capturing…",
            TextAlign = SD.ContentAlignment.MiddleCenter,
            AccessibleName = "Scrolling capture preview status",
            AccessibleDescription = "Live capture progress and recovery guidance.",
            AccessibleRole = WF.AccessibleRole.StaticText,
        };
        _pic = new WF.PictureBox
        {
            Name = "ScrollCapturePreview",
            Dock = WF.DockStyle.Fill,
            SizeMode = WF.PictureBoxSizeMode.Zoom,
            BackColor = ThemePalette.WindowBg,
            AccessibleName = "Scrolling capture preview",
            AccessibleDescription = "A local preview of the stitched screenshot.",
            AccessibleRole = WF.AccessibleRole.Graphic,
        };
        Controls.Add(_pic);
        Controls.Add(_caption);

        Shown += (_, _) => PositionBottomRight(regionScreen);
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= CaptureExclusion.WS_EX_LAYERED | CaptureExclusion.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureExclusion.ApplyLayeredOpaque(Handle);
        CaptureExclusion.SetLayeredAlpha(Handle, 0); // the fade in OnShown brings it up
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _fade = ScrollChromeFade.In(SetChromeAlpha);
    }

    /// <summary>Fades the preview out and completes once it is gone; the caller then closes.</summary>
    public Task FadeOutAsync()
    {
        if (IsDisposed || !IsHandleCreated) return Task.CompletedTask;
        _fade?.Dispose();
        return ScrollChromeFade.Out(SetChromeAlpha, out _fade);
    }

    private void SetChromeAlpha(byte alpha)
    {
        if (IsDisposed || !IsHandleCreated) return;
        CaptureExclusion.SetLayeredAlpha(Handle, alpha);
    }

    /// <summary>Takes ownership of <paramref name="image"/> (disposes the previous one).</summary>
    public void SetImage(SD.Bitmap image, string caption)
    {
        if (IsDisposed) { image.Dispose(); return; }
        var old = _pic.Image;
        _pic.Image = image;
        old?.Dispose();
        if (!_hintShowing)
            _caption.Text = caption;
    }

    /// <summary>Live progress text under the preview ("12 frames — 4000px").</summary>
    public void SetCaption(string text)
    {
        if (IsDisposed || _hintShowing) return;
        _caption.ForeColor = ThemePalette.TextSecondary;
        _caption.Text = text;
    }

    /// <summary>The hint shown under the preview, mirroring the controls bar — the user is
    /// usually watching this panel while scrolling.</summary>
    public void SetHint(ScrollHint hint)
    {
        if (IsDisposed) return;
        _hintShowing = hint != ScrollHint.None;
        if (!_hintShowing)
        {
            _caption.ForeColor = ThemePalette.TextSecondary;
            return;
        }
        _caption.ForeColor = ScrollHintText.ColorFor(hint);
        _caption.Text = ScrollHintText.For(hint) ?? _caption.Text;
    }

    protected override void OnFormClosed(WF.FormClosedEventArgs e)
    {
        _fade?.Dispose();
        _fade = null;
        _pic.Image?.Dispose();
        _pic.Image = null;
        base.OnFormClosed(e);
    }

    private void PositionBottomRight(SD.Rectangle region)
    {
        var mon = WF.Screen.FromRectangle(region).Bounds;
        int inset = S(20);
        Location = new SD.Point(mon.Right - Width - inset, mon.Bottom - Height - inset);
    }
}
