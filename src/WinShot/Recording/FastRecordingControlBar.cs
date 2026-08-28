using System.Diagnostics;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastRecordingControlBar : WF.Form
{
    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private static readonly SD.Color ButtonBack = ThemePalette.SurfaceAlt;
    private static readonly SD.Color StopBack = ThemePalette.Accent;
    private static readonly SD.Color RecordingRed = ThemePalette.Red;
    private static readonly SD.Color PausedAmber = ThemePalette.Warn;

    private readonly Stopwatch _elapsed = new();
    // 50ms is fast enough for a smooth breathing pulse without holding the
    // high-resolution clock for the whole recording.
    private readonly WF.Timer _timer = new() { Interval = 50 };
    private readonly DotControl _dot;
    private readonly WF.Label _elapsedText;
    private readonly DarkButton _pause;
    private readonly DarkButton _restart;
    private readonly DarkButton _stop;
    private readonly DarkButton _cancel;
    private readonly SD.Rectangle? _recordingRegion;
    private readonly bool _showTimer;
    private readonly double _scale;
    private bool _actionTaken;
    private bool _paused;

    public FastRecordingControlBar(SD.Rectangle? recordingRegion = null, bool showTimer = true)
    {
        _recordingRegion = recordingRegion;
        _showTimer = showTimer;
        // Point-based fonts render at the monitor's DPI, so the fixed-pixel button
        // layout must scale with the target monitor or labels truncate on 125%/150%
        // displays. (AutoScaleMode.Dpi can't: it only reacts to DPI *changes*.)
        SD.Rectangle targetScreen = recordingRegion is SD.Rectangle r
            ? WF.Screen.FromRectangle(r).Bounds
            : WF.Screen.FromPoint(WF.Cursor.Position).Bounds;
        _scale = RecordingMonitorDpi.ScaleFor(targetScreen);
        // Create the window ON its target monitor: born at (0,0) on the primary and moved
        // later, PerMonitorV2 fires WM_DPICHANGED on the move and rescales the window on
        // top of the pre-applied _scale (double-scaled bar on mixed-DPI setups).
        Location = new SD.Point(targetScreen.X, targetScreen.Y);
        AutoScaleMode = WF.AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = WF.AutoSizeMode.GrowAndShrink;
        BackColor = Back;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Opacity = 0.96;
        Padding = new WF.Padding(S(14), S(8), S(14), S(8));
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        var row = new WF.FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            BackColor = Back,
            FlowDirection = WF.FlowDirection.LeftToRight,
            Margin = WF.Padding.Empty,
            Padding = WF.Padding.Empty,
            WrapContents = false,
        };

        _dot = new DotControl
        {
            DotColor = RecordingRed,
            Margin = new WF.Padding(S(2), S(7), S(8), 0),
            Size = new SD.Size(S(10), S(10)),
        };
        _dot.Visible = showTimer;
        row.Controls.Add(_dot);

        _elapsedText = new WF.Label
        {
            AutoSize = false,
            Font = new SD.Font("Consolas", 10f, SD.FontStyle.Regular),
            ForeColor = SD.Color.White,
            Margin = new WF.Padding(0, S(4), S(12), 0),
            Size = new SD.Size(S(66), S(22)),
            Text = "00:00",
            TextAlign = SD.ContentAlignment.MiddleLeft,
        };
        _elapsedText.Visible = showTimer;
        row.Controls.Add(_elapsedText);

        _pause = Button("Pause", ButtonBack, widthFor: "Resume");
        _pause.Click += (_, _) => TogglePause();
        row.Controls.Add(_pause);

        _restart = Button("Restart", ButtonBack);
        _restart.Click += (_, _) => RaiseOnce(RestartRequested);
        row.Controls.Add(_restart);

        _stop = Button("Stop", StopBack);
        _stop.Click += (_, _) => RaiseOnce(StopRequested);
        row.Controls.Add(_stop);

        _cancel = Button("Cancel", ButtonBack);
        _cancel.Click += (_, _) => RaiseOnce(CancelRequested);
        row.Controls.Add(_cancel);

        Controls.Add(row);
        // A form's Padding feeds AutoSize but does NOT position non-docked children —
        // without this the row hugs the top-left and all the padding lands bottom-right.
        row.Location = new SD.Point(S(14), S(8));

        MouseDown += OnDragMouseDown;
        row.MouseDown += OnDragMouseDown;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == WF.Keys.Escape)
                RaiseOnce(CancelRequested);
        };
        _timer.Tick += (_, _) =>
        {
            string elapsed = FormatElapsed(_elapsed.Elapsed);
            if (_elapsedText.Text != elapsed)
                _elapsedText.Text = elapsed;
            // Eased breathing instead of a binary blink: alpha rides a 1.6s sine.
            double wave = (1 + Math.Sin(2 * Math.PI * Environment.TickCount64 / 1600.0)) / 2;
            _dot.DotColor = _paused
                ? PausedAmber
                : SD.Color.FromArgb(120 + (int)Math.Round(135 * wave), RecordingRed);
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
        PositionBottomCenter();
        ExcludeFromCapture();
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
        base.OnPaint(e);
        PopupChrome.DrawBorder(e.Graphics, ClientSize, Height / 2);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnClosed(e);
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private void PositionBottomCenter()
    {
        SD.Rectangle area = _recordingRegion is SD.Rectangle region
            ? WF.Screen.FromRectangle(region).WorkingArea
            : WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        Location = RecordingControlBarPlacement.BottomCenter(area, Size);
    }

    private void TogglePause()
    {
        if (_actionTaken)
            return;

        _paused = !_paused;
        if (_paused)
        {
            _elapsed.Stop();
            _pause.Text = "Resume";
            _dot.DotColor = PausedAmber;
            PauseRequested?.Invoke();
        }
        else
        {
            _elapsed.Start();
            _pause.Text = "Pause";
            _dot.DotColor = RecordingRed;
            ResumeRequested?.Invoke();
        }
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
        {
            _elapsed.Stop();
            _pause.Text = "Resume";
        }
        else
        {
            _elapsed.Start();
            _pause.Text = "Pause";
        }
    }

    private void RaiseOnce(Action? action)
    {
        if (_actionTaken)
            return;

        _actionTaken = true;
        _pause.Enabled = false;
        _restart.Enabled = false;
        _stop.Enabled = false;
        _cancel.Enabled = false;
        action?.Invoke();
    }

    private void OnDragMouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Left)
            Native.ReleaseCaptureAndDrag(Handle);
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

    private int S(int logical) => (int)Math.Round(logical * _scale);

    private DarkButton Button(string text, SD.Color fillColor, string? widthFor = null)
    {
        // Width from measured content, not label length, so padding stays uniform
        // (widthFor sizes for the button's widest state, e.g. Pause -> Resume).
        // Measure with a pixel-unit font at the TARGET scale: DefaultFont measures at the
        // primary monitor's DPI, and multiplying that by _scale double-applied the factor
        // whenever the primary itself is scaled.
        using var measureFont = new SD.Font("Segoe UI", Math.Max(9, S(12)), SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
        SD.Size measured = WF.TextRenderer.MeasureText(widthFor ?? text, measureFont);
        return new DarkButton
        {
            Scale = _scale,
            BackColor = Back,
            CornerRadius = 13,
            FillColor = fillColor,
            Margin = new WF.Padding(S(3), 0, S(3), 0),
            Size = new SD.Size(Math.Max(S(54), measured.Width + S(28)), S(26)),
            // A mouse HUD: nothing should silently hold keyboard focus and wear a ring.
            TabStop = false,
            Text = text,
        };
    }

    private sealed class DotControl : WF.Control
    {
        private SD.Color _dotColor;

        public SD.Color DotColor
        {
            get => _dotColor;
            set
            {
                if (_dotColor == value)
                    return;
                _dotColor = value;
                Invalidate();
            }
        }

        public DotControl()
        {
            SetStyle(
                WF.ControlStyles.AllPaintingInWmPaint |
                WF.ControlStyles.OptimizedDoubleBuffer |
                WF.ControlStyles.ResizeRedraw |
                WF.ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaint(WF.PaintEventArgs e)
        {
            using var brush = new SD.SolidBrush(_dotColor);
            e.Graphics.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
        }
    }

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

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }
}
