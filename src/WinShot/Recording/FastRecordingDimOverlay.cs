using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public static class RecordingDimOverlayLayout
{
    public static IReadOnlyList<SD.Rectangle> OutsideRecordingArea(
        SD.Rectangle display,
        SD.Rectangle recordingArea)
    {
        SD.Rectangle cut = SD.Rectangle.Intersect(display, recordingArea);
        if (cut.Width <= 0 || cut.Height <= 0)
            return [display];

        var result = new List<SD.Rectangle>(4);
        Add(result, new SD.Rectangle(display.Left, display.Top, display.Width, cut.Top - display.Top));
        Add(result, new SD.Rectangle(display.Left, cut.Bottom, display.Width, display.Bottom - cut.Bottom));
        Add(result, new SD.Rectangle(display.Left, cut.Top, cut.Left - display.Left, cut.Height));
        Add(result, new SD.Rectangle(cut.Right, cut.Top, display.Right - cut.Right, cut.Height));
        return result;
    }

    private static void Add(List<SD.Rectangle> result, SD.Rectangle rect)
    {
        if (rect.Width > 0 && rect.Height > 0)
            result.Add(rect);
    }
}

/// <summary>Which side of the recorded rectangle a marker strip hugs.</summary>
public enum RecordingRegionMarkerEdge
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>
/// Geometry for the border-only recording marker: four thin strips laid just OUTSIDE the
/// recorded rectangle, clipped to the desktop. Pure, because the one invariant that matters —
/// no strip may ever touch a recorded pixel — is worth a test rather than an eyeball.
/// </summary>
public static class RecordingRegionMarkerLayout
{
    public static IReadOnlyList<(RecordingRegionMarkerEdge Edge, SD.Rectangle Bounds)> Strips(
        SD.Rectangle bounds,
        SD.Rectangle recordingArea,
        int thickness)
    {
        var strips = new List<(RecordingRegionMarkerEdge, SD.Rectangle)>(4);
        if (thickness <= 0 || recordingArea.Width <= 0 || recordingArea.Height <= 0)
            return strips;

        SD.Rectangle r = recordingArea;
        // The left and right strips run the full corner-to-corner height so the corners are
        // drawn once and by one strip each.
        Add(strips, RecordingRegionMarkerEdge.Top, bounds,
            new SD.Rectangle(r.Left, r.Top - thickness, r.Width, thickness));
        Add(strips, RecordingRegionMarkerEdge.Bottom, bounds,
            new SD.Rectangle(r.Left, r.Bottom, r.Width, thickness));
        Add(strips, RecordingRegionMarkerEdge.Left, bounds,
            new SD.Rectangle(r.Left - thickness, r.Top - thickness, thickness, r.Height + (thickness * 2)));
        Add(strips, RecordingRegionMarkerEdge.Right, bounds,
            new SD.Rectangle(r.Right, r.Top - thickness, thickness, r.Height + (thickness * 2)));
        return strips;
    }

    private static void Add(
        List<(RecordingRegionMarkerEdge, SD.Rectangle)> strips,
        RecordingRegionMarkerEdge edge,
        SD.Rectangle bounds,
        SD.Rectangle strip)
    {
        SD.Rectangle clipped = SD.Rectangle.Intersect(bounds, strip);
        if (clipped.Width > 0 && clipped.Height > 0)
            strips.Add((edge, clipped));
    }
}

/// <summary>
/// Marks the recorded rectangle while a take runs, in one of two modes:
///
/// <list type="bullet">
/// <item>dimmed — capture-excluded black bands over everything outside the rectangle, with an
/// accent line on the inner edge of each band;</item>
/// <item>border-only — nothing dimmed, just a red 2 px line with a soft outer glow hugging the
/// rectangle, slow-pulsing so it reads as live rather than as a window decoration.</item>
/// </list>
///
/// Either way the pixels being recorded are never covered: every surface is laid outside the
/// rectangle, which is what keeps the output clean even where display-affinity exclusion is
/// unavailable.
/// </summary>
public sealed class FastRecordingDimOverlay : IDisposable
{
    /// <summary>Peak opacity of the dim bands. The entrance ramps up to this.</summary>
    private const double DimOpacity = 0.36;

    private readonly List<DimSurface> _surfaces = new();
    private readonly List<MarkerSurface> _markers = new();
    private IDisposable? _entrance;
    private IDisposable? _exit;
    private bool _fadingOut;

    public FastRecordingDimOverlay(SD.Rectangle recordingArea)
        : this(recordingArea, dimScreen: true)
    {
    }

    public FastRecordingDimOverlay(SD.Rectangle recordingArea, bool dimScreen)
    {
        if (dimScreen)
        {
            foreach (WF.Screen screen in WF.Screen.AllScreens)
            {
                foreach (SD.Rectangle rect in RecordingDimOverlayLayout.OutsideRecordingArea(screen.Bounds, recordingArea))
                    _surfaces.Add(new DimSurface(rect, recordingArea));
            }
            return;
        }

        // Border-only: the glow is scaled to the recorded region's monitor so it reads the
        // same physical width on a 150% display as on a 100% one.
        int thickness = RecordingMonitorDpi.Px(
            MarkerSurface.ThicknessDip,
            RecordingMonitorDpi.ScaleFor(recordingArea));
        SD.Rectangle desktop = CaptureService.VirtualScreen;
        foreach ((RecordingRegionMarkerEdge edge, SD.Rectangle bounds) in
                 RecordingRegionMarkerLayout.Strips(desktop, recordingArea, thickness))
        {
            _markers.Add(new MarkerSurface(bounds, edge, thickness));
        }
    }

    public void Show()
    {
        foreach (DimSurface surface in _surfaces)
        {
            surface.Opacity = 0;
            surface.Show();
        }
        foreach (MarkerSurface marker in _markers)
        {
            marker.SetFade(0);
            marker.Show();
        }

        if (_surfaces.Count == 0 && _markers.Count == 0)
            return;

        // One tween for every band AND every marker strip, so both dim modes arrive instead
        // of snapping on — the border-only marker used to pop in at full alpha and then fade
        // out on the way, which is an entrance and an exit that do not match. Only one
        // timer-resolution holder is taken for the whole overlay.
        _entrance?.Dispose();
        _entrance = Motion.Run(
            Motion.QuickMs,
            Motion.EaseOutCubic,
            amount =>
            {
                foreach (DimSurface surface in _surfaces)
                {
                    if (!surface.IsDisposed)
                        surface.Opacity = DimOpacity * amount;
                }
                foreach (MarkerSurface marker in _markers)
                {
                    if (!marker.IsDisposed)
                        marker.SetFade(amount);
                }
            },
            () => _entrance = null);
    }

    /// <summary>
    /// Leaves the way it arrived, then disposes itself. Used when a take stops: the marker
    /// vanishing in the same frame as the last recorded one is the "everything tears down at
    /// once" flash this whole change exists to remove.
    /// </summary>
    public void FadeOutAndDispose()
    {
        if (_fadingOut)
            return;
        _fadingOut = true;
        _entrance?.Dispose();
        _entrance = null;

        _exit = Motion.Run(
            Motion.QuickMs,
            Motion.EaseInCubic,
            amount =>
            {
                double remaining = 1 - amount;
                foreach (DimSurface surface in _surfaces)
                {
                    if (!surface.IsDisposed)
                        surface.Opacity = DimOpacity * remaining;
                }
                foreach (MarkerSurface marker in _markers)
                {
                    if (!marker.IsDisposed)
                        marker.SetFade(remaining);
                }
            },
            Dispose);
    }

    public void Dispose()
    {
        _entrance?.Dispose();
        _entrance = null;
        _exit?.Dispose();
        _exit = null;
        foreach (DimSurface surface in _surfaces)
        {
            try { surface.Close(); surface.Dispose(); }
            catch (Exception ex) { Log.Error("Failed to close recording dim surface", ex); }
        }
        _surfaces.Clear();
        foreach (MarkerSurface marker in _markers)
        {
            try { marker.Close(); marker.Dispose(); }
            catch (Exception ex) { Log.Error("Failed to close recording region marker", ex); }
        }
        _markers.Clear();
    }

    /// <summary>
    /// One border strip: a layered ARGB window (so the glow's falloff blends against the real
    /// desktop instead of against a key colour) painting a solid line on the edge that abuts
    /// the recorded rectangle and a fading glow away from it.
    /// </summary>
    private sealed class MarkerSurface : AlphaOverlayWindow
    {
        /// <summary>Line plus glow, in logical px.</summary>
        public const int ThicknessDip = 10;

        private static readonly SD.Color Marker = ThemePalette.Red;

        /// <summary>Period of the breathing sine, matching the control bar's dot exactly.</summary>
        private const double PulsePeriodMs = 1600.0;

        // A binary blink reads as a fault; the same eased 1.6s sine the control bar's dot
        // rides says "live" instead. A plain 50ms timer, not Motion.Run: this never ends, so
        // it must not hold the process-wide high-resolution clock for the whole take. Not
        // holding the clock and honouring the accessibility setting are separate decisions,
        // though — with visual effects off the timer never starts and the line holds steady.
        private readonly WF.Timer _pulse = new() { Interval = 50 };
        private readonly SD.Rectangle _bounds;
        private readonly RecordingRegionMarkerEdge _edge;
        private readonly int _thickness;
        private int _alpha = -1;
        private double _fade = 1;

        public MarkerSurface(SD.Rectangle bounds, RecordingRegionMarkerEdge edge, int thickness)
        {
            _bounds = bounds;
            _edge = edge;
            _thickness = thickness;
            Location = bounds.Location;
            ClientSize = bounds.Size;
            _pulse.Tick += (_, _) => Render();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ExcludeFromCapture();
            Render();
            if (Motion.AnimationsEnabled())
                _pulse.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            _pulse.Stop();
            _pulse.Dispose();
            base.OnClosed(e);
        }

        /// <summary>Global multiplier on the strip's alpha, so the owner can fade every strip
        /// out with one tween. A layered window cannot use Form.Opacity — that is
        /// SetLayeredWindowAttributes, which fights UpdateLayeredWindow — so the fade lives in
        /// the bitmap.</summary>
        public void SetFade(double fade)
        {
            _fade = Math.Clamp(fade, 0, 1);
            _alpha = -1; // force the next frame through even if the pulse landed on the same step
            Render();
        }

        private void Render()
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            double wave = Motion.AnimationsEnabled()
                ? (1 + Math.Sin(2 * Math.PI * Environment.TickCount64 / PulsePeriodMs)) / 2
                : 1;
            int alpha = (int)Math.Round((150 + (105 * wave)) * _fade);
            if (alpha == _alpha)
                return;
            _alpha = alpha;

            using var frame = new SD.Bitmap(
                Math.Max(1, _bounds.Width),
                Math.Max(1, _bounds.Height),
                PixelFormat.Format32bppArgb);
            using (SD.Graphics g = SD.Graphics.FromImage(frame))
            {
                g.SmoothingMode = SmoothingMode.None;
                bool horizontal = _edge is RecordingRegionMarkerEdge.Top or RecordingRegionMarkerEdge.Bottom;
                // Row 0 is the edge touching the region; alpha falls off away from it.
                for (int step = 0; step < _thickness; step++)
                {
                    int stepAlpha = step < 2
                        ? alpha
                        : (int)Math.Round(alpha * 0.55 * Math.Pow(1 - ((step - 1) / (double)_thickness), 2));
                    if (stepAlpha <= 0)
                        continue;
                    using var pen = new SD.Pen(SD.Color.FromArgb(Math.Clamp(stepAlpha, 0, 255), Marker), 1);
                    int offset = OffsetFromRegion(step, horizontal);
                    if (horizontal)
                        g.DrawLine(pen, 0, offset, frame.Width, offset);
                    else
                        g.DrawLine(pen, offset, 0, offset, frame.Height);
                }
            }

            Present(frame, _bounds.Location);
        }

        /// <summary>Distance <paramref name="step"/> pixels away from the recorded rectangle,
        /// expressed in this strip's own coordinates.</summary>
        private int OffsetFromRegion(int step, bool horizontal)
        {
            int extent = horizontal ? _bounds.Height : _bounds.Width;
            bool growsInward = _edge is RecordingRegionMarkerEdge.Bottom or RecordingRegionMarkerEdge.Right;
            return growsInward
                ? Math.Min(extent - 1, step)
                : Math.Max(0, extent - 1 - step);
        }

        private void ExcludeFromCapture()
        {
            try
            {
                if (!SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture))
                    Log.Info("Display-affinity exclusion unavailable for a recording region marker; geometry still stays outside the recorded rectangle");
            }
            catch (Exception ex)
            {
                Log.Error("Could not request capture exclusion for a recording region marker", ex);
            }
        }

        private const uint WdaExcludeFromCapture = 0x11;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }

    private sealed class DimSurface : WF.Form
    {
        private readonly SD.Rectangle _recordingArea;

        public DimSurface(SD.Rectangle bounds, SD.Rectangle recordingArea)
        {
            _recordingArea = recordingArea;
            AutoScaleMode = WF.AutoScaleMode.None;
            BackColor = SD.Color.Black;
            Bounds = bounds;
            DoubleBuffered = true;
            FormBorderStyle = WF.FormBorderStyle.None;
            Opacity = DimOpacity;
            ShowInTaskbar = false;
            StartPosition = WF.FormStartPosition.Manual;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation => true;

        /// <summary>
        /// Marks the recorded rectangle's edge with a crisp accent line wherever this
        /// dim band abuts it, so the user can see exactly what is being captured. The
        /// line lives on the dim window — outside the recorded pixels — and the surface
        /// is capture-excluded anyway, so the recording stays clean. (Opacity applies,
        /// so the pen is drawn strong to survive the 36% window alpha.)
        /// </summary>
        protected override void OnPaint(WF.PaintEventArgs e)
        {
            base.OnPaint(e);
            SD.Rectangle r = Bounds;
            SD.Rectangle rec = _recordingArea;
            int left = Math.Max(r.Left, rec.Left) - r.Left;
            int right = Math.Min(r.Right, rec.Right) - r.Left;
            int top = Math.Max(r.Top, rec.Top) - r.Top;
            int bottom = Math.Min(r.Bottom, rec.Bottom) - r.Top;
            using var pen = new SD.Pen(ThemePalette.Accent, 2);
            if (r.Bottom == rec.Top && right > left) // band above the recording
                e.Graphics.DrawLine(pen, left, Height - 1, right, Height - 1);
            if (r.Top == rec.Bottom && right > left) // band below
                e.Graphics.DrawLine(pen, left, 0, right, 0);
            if (r.Right == rec.Left && bottom > top) // band to the left
                e.Graphics.DrawLine(pen, Width - 1, top, Width - 1, bottom);
            if (r.Left == rec.Right && bottom > top) // band to the right
                e.Graphics.DrawLine(pen, 0, top, 0, bottom);
        }

        protected override WF.CreateParams CreateParams
        {
            get
            {
                WF.CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExTransparent | WsExNoActivate | WsExToolWindow;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                if (!SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture))
                    Log.Info("Display-affinity exclusion unavailable for a recording dim surface; geometry still stays outside the recorded rectangle");
            }
            catch (Exception ex)
            {
                Log.Error("Could not request capture exclusion for recording dim surface", ex);
            }
        }

        private const int WsExTransparent = 0x20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x80;
        private const uint WdaExcludeFromCapture = 0x11;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }
}
