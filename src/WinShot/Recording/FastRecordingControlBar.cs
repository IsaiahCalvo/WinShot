using System.Diagnostics;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastRecordingControlBar : WF.Form
{
    private static readonly SD.Color Back = SD.Color.FromArgb(43, 43, 43);
    private static readonly SD.Color ButtonBack = SD.Color.FromArgb(58, 58, 58);
    private static readonly SD.Color ButtonHot = SD.Color.FromArgb(79, 79, 79);
    private static readonly SD.Color StopBack = SD.Color.FromArgb(45, 125, 255);
    private static readonly SD.Color StopHot = SD.Color.FromArgb(77, 163, 255);
    private static readonly SD.Color RecordingRed = SD.Color.FromArgb(255, 82, 82);
    private static readonly SD.Color PausedAmber = SD.Color.FromArgb(255, 176, 32);

    private readonly Stopwatch _elapsed = new();
    private readonly WF.Timer _timer = new() { Interval = 250 };
    private readonly DotControl _dot;
    private readonly WF.Label _elapsedText;
    private readonly WF.Button _pause;
    private readonly WF.Button _stop;
    private readonly WF.Button _cancel;
    private bool _actionTaken;
    private bool _paused;
    private bool _pulseDim;

    public FastRecordingControlBar()
    {
        AutoScaleMode = WF.AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = WF.AutoSizeMode.GrowAndShrink;
        BackColor = Back;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Opacity = 0.96;
        Padding = new WF.Padding(14, 8, 14, 8);
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;

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
            Margin = new WF.Padding(2, 7, 8, 0),
            Size = new SD.Size(10, 10),
        };
        row.Controls.Add(_dot);

        _elapsedText = new WF.Label
        {
            AutoSize = false,
            Font = new SD.Font("Consolas", 10f, SD.FontStyle.Regular),
            ForeColor = SD.Color.White,
            Margin = new WF.Padding(0, 4, 12, 0),
            Size = new SD.Size(48, 22),
            Text = "00:00",
            TextAlign = SD.ContentAlignment.MiddleLeft,
        };
        row.Controls.Add(_elapsedText);

        _pause = Button("Pause", ButtonBack, ButtonHot);
        _pause.Click += (_, _) => TogglePause();
        row.Controls.Add(_pause);

        _stop = Button("Stop", StopBack, StopHot);
        _stop.Click += (_, _) => RaiseOnce(StopRequested);
        row.Controls.Add(_stop);

        _cancel = Button("Cancel", ButtonBack, ButtonHot);
        _cancel.Click += (_, _) => RaiseOnce(CancelRequested);
        row.Controls.Add(_cancel);

        Controls.Add(row);

        MouseDown += OnDragMouseDown;
        row.MouseDown += OnDragMouseDown;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == WF.Keys.Escape)
                RaiseOnce(CancelRequested);
        };
        _timer.Tick += (_, _) =>
        {
            _elapsedText.Text = FormatElapsed(_elapsed.Elapsed);
            _pulseDim = !_pulseDim;
            _dot.DotColor = _paused
                ? PausedAmber
                : (_pulseDim ? SD.Color.FromArgb(130, RecordingRed) : RecordingRed);
        };
    }

    public event Action? StopRequested;
    public event Action? CancelRequested;
    public event Action? PauseRequested;
    public event Action? ResumeRequested;

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateWindowRegion();
        PositionBottomCenter();
        ExcludeFromCapture();
        _elapsed.Start();
        _timer.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnClosed(e);
    }

    private static string FormatElapsed(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private void PositionBottomCenter()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        Location = new SD.Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Bottom - Height - 24);
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

    private void RaiseOnce(Action? action)
    {
        if (_actionTaken)
            return;

        _actionTaken = true;
        _pause.Enabled = false;
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

        IntPtr regionHandle = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 36, 36);
        Region = SD.Region.FromHrgn(regionHandle);
        Native.DeleteObject(regionHandle);
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

    private static WF.Button Button(string text, SD.Color backColor, SD.Color hotColor)
    {
        var button = new WF.Button
        {
            AutoSize = false,
            BackColor = backColor,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = SD.Color.White,
            Margin = new WF.Padding(3, 0, 3, 0),
            Size = new SD.Size(text.Length > 5 ? 64 : 54, 26),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hotColor;
        return button;
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
