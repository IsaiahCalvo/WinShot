using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

public sealed class FastSelfTimerWindow : WF.Form
{
    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private static readonly SD.Color Border = ThemePalette.BorderStrong;
    private static readonly SD.Color Ring = ThemePalette.Accent;
    private static readonly SD.Color TextColor = ThemePalette.TextPrimary;

    // The delay is a real wait, so it stays on a plain one-second timer and keeps working
    // with animation turned off system-wide; only the ring's sweep and the digit's pop ride
    // Motion, one tween per second. Shared with the recording countdown through CountdownRing.
    private readonly WF.Timer _timer = new() { Interval = 1000 };
    private readonly int _totalSeconds;
    private IDisposable? _second;
    private double _secondFraction;
    private int _remaining;

    // Scales the badge (and its pixel-unit digits) to the cursor monitor, so it
    // reads the same physical size on 100% and 150% displays.
    private readonly double _scale;

    private int S(int logical) => (int)Math.Round(logical * _scale);

    public FastSelfTimerWindow(int seconds)
    {
        _remaining = SelfTimerOptions.ClampDelaySeconds(seconds);
        _totalSeconds = _remaining;
        SD.Rectangle timerScreen = WF.Screen.FromPoint(WF.Cursor.Position).Bounds;
        _scale = WinShot.Recording.RecordingMonitorDpi.ScaleFor(timerScreen);
        // Create on the target monitor so the OnShown move never fires a WM_DPICHANGED
        // rescale on top of the pre-applied _scale (see FastRecordingControlBar).
        Location = new SD.Point(timerScreen.X, timerScreen.Y);

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        ClientSize = new SD.Size(S(150), S(150));
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        Opacity = 0.9;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _timer.Stop();
                _second?.Dispose();
                _second = null;
                Close();
                return;
            }

            RunSecond();
            Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;


    public static Task RunAsync(int seconds)
    {
        if (!SelfTimerOptions.ShouldRunDelay(seconds))
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var win = new FastSelfTimerWindow(SelfTimerOptions.ClampDelaySeconds(seconds));
        win.Closed += async (_, _) =>
        {
            await Task.Delay(120).ConfigureAwait(false);
            tcs.TrySetResult();
        };
        win.Show();
        return tcs.Task;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateWindowRegion();
        MakeClickThrough();
        PositionCenterScreen();
        _timer.Start();
        RunSecond();
    }

    /// <summary>One tween per second drives the ring's sweep and the digit's landing. It is
    /// restarted by the tick, so it can never drift away from the clock that owns the delay.</summary>
    private void RunSecond()
    {
        _second?.Dispose();
        _secondFraction = 0;
        _second = WinShot.Core.Motion.Run(
            1000,
            WinShot.Core.Motion.Linear,
            amount =>
            {
                // With animations off Motion.Run lands 1.0 inline, which parked the ring a
                // whole second ahead of the digit inside it — a 3-second countdown showed a
                // third already consumed on its first frame. The step belongs at the START of
                // the second it describes.
                _secondFraction = WinShot.Core.Motion.AnimationsEnabled() ? amount : 0;
                if (!IsDisposed)
                    Invalidate();
            },
            () => _second = null);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopClocks();
        base.OnClosed(e);
    }

    /// <summary>
    /// Form.Dispose() does not raise Closed, so the tween handle cannot be released only in
    /// OnClosed: a Dispose without a Close left timeBeginPeriod(1) raised for the life of the
    /// process. Every call site closes today; this is what keeps that from mattering.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            StopClocks();
        base.Dispose(disposing);
    }

    private void StopClocks()
    {
        _second?.Dispose();
        _second = null;
        _timer.Stop();
        _timer.Dispose();
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Back);

        // Real bold face — Bold on top of the Semibold family makes GDI synthesize a
        // smeared faux-bold at exactly the size where it shows most.
        using var font = WinShot.Core.ThemePalette.UiFontPx(S(72), SD.FontStyle.Bold);
        WinShot.Core.CountdownRing.Draw(
            e.Graphics,
            ClientSize,
            Math.Max(2, S(2)),
            WinShot.Core.CountdownRing.Progress(_totalSeconds, _remaining, _secondFraction),
            _remaining.ToString(),
            font,
            TextColor,
            Border,
            Ring,
            // The optical-centring nudge rides the same scale as the numeral it lifts; left
            // raw it was 3 px against a 72 px digit at 100% and against a 144 px one at 200%.
            new SD.Rectangle(0, 0, Width, Height - S(6)),
            WinShot.Core.CountdownRing.DigitScale(_secondFraction));
    }

    private void PositionCenterScreen()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        Location = new SD.Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private void MakeClickThrough()
    {
        int style = GetWindowLongW(Handle, GwlExstyle);
        SetWindowLongW(Handle, GwlExstyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        IntPtr regionHandle = CreateEllipticRgn(0, 0, Width + 1, Height + 1);
        Region = SD.Region.FromHrgn(regionHandle);
        DeleteObject(regionHandle);
    }

    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
}
