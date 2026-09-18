using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastRecordingCountdownWindow : WF.Form
{
    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private static readonly SD.Color Ring = ThemePalette.Accent;
    private static readonly SD.Color RingTrack = ThemePalette.BorderStrong;
    private static readonly SD.Color TextColor = SD.Color.White;
    // The palette's own muted text. It used to be 170/170/170, a grey typed in by hand next to
    // the token it was standing in for.
    private static readonly SD.Color MutedText = ThemePalette.TextSecondary;

    // The delay itself is a real wait, so it stays on a plain one-second timer and keeps
    // working with animation turned off system-wide. Only the ring's sweep and the digit's
    // pop ride Motion, one tween per second, so with animation off they step instead.
    private readonly WF.Timer _timer = new() { Interval = 1000 };
    private readonly SD.Rectangle _regionPx;
    private readonly int _totalSeconds;
    private IDisposable? _second;
    private double _secondFraction;
    private int _remaining;
    private bool _done;

    // Scales the badge (and its pixel-unit digits) to the recorded region's monitor,
    // so it reads the same physical size on 100% and 150% displays.
    private readonly double _scale;

    private int S(int logical) => (int)Math.Round(logical * _scale);

    public FastRecordingCountdownWindow(int seconds, SD.Rectangle regionScreenPx)
    {
        _remaining = Math.Max(1, seconds);
        _totalSeconds = _remaining;
        _regionPx = regionScreenPx;
        _scale = RecordingMonitorDpi.ScaleFor(regionScreenPx);
        // Create on the target monitor so the OnShown move never fires a WM_DPICHANGED
        // rescale on top of the pre-applied _scale (see FastRecordingControlBar).
        Location = new SD.Point(regionScreenPx.X, regionScreenPx.Y);

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        ClientSize = new SD.Size(S(170), S(170));
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        _timer.Tick += OnTick;
        MouseDown += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
                CancelCountdown();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == WF.Keys.Escape)
                CancelCountdown();
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateWindowRegion();
        PositionOverRegion();
        Activate();
        Focus();
        _timer.Start();
        RunSecond();
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

        // Real bold face, not faux-bold synthesized over Semibold.
        using var countFont = WinShot.Core.ThemePalette.UiFontPx(S(84), SD.FontStyle.Bold);
        CountdownRing.Draw(
            e.Graphics,
            ClientSize,
            Math.Max(3, S(3)),
            CountdownRing.Progress(_totalSeconds, _remaining, _secondFraction),
            _remaining.ToString(),
            countFont,
            TextColor,
            RingTrack,
            Ring,
            new SD.Rectangle(0, S(10), Width, S(120)),
            CountdownRing.DigitScale(_secondFraction));

        // The badge is a 170px circle, so the ellipse leaves this row about 130px of width.
        // "Click or Esc to cancel" needs ~165px at the title step and ~150px at the 11pt it
        // shipped at — both clipped flat at each end. The body step is what actually fits,
        // and EndEllipsis makes any future string say so instead of losing its last word.
        using var hintFont = ThemePalette.ScaledUiFont(ThemePalette.TextBodyPt, _scale);
        WF.TextRenderer.DrawText(
            e.Graphics,
            "Click or Esc to cancel",
            hintFont,
            HintBounds(ClientSize, _scale),
            MutedText,
            WF.TextFormatFlags.HorizontalCenter |
            WF.TextFormatFlags.VerticalCenter |
            WF.TextFormatFlags.SingleLine |
            WF.TextFormatFlags.NoPadding |
            WF.TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// Where the cancel hint goes. The window IS the ring — the form's Region is an ellipse — so
    /// this row's usable width is the circle's CHORD at its own height, not the window's width.
    /// It used to take the full width, and at 100 % the sentence is 118 px against a 115 px
    /// chord: both ends were painted on top of the accent ring. Raised to where the chord opens
    /// wide enough for the whole sentence, and clamped to that chord, so no future string can
    /// cross the ring however long it grows — it ellipses instead.
    /// </summary>
    internal static SD.Rectangle HintBounds(SD.Size client, double scale)
    {
        int Px(int logical) => (int)Math.Round(logical * scale);
        int height = Px(24);
        int top = client.Height - Px(58);
        double radius = (Math.Min(client.Width, client.Height) / 2.0) - Math.Max(3, Px(3));
        // Measured at the row's BOTTOM edge, the corner nearest the ring — the chord at the row's
        // middle is wider than the row's own corners have, and using it put the sentence's tail
        // back on the arc.
        double dy = Math.Abs(top + height - (client.Height / 2.0));
        int half = (int)Math.Floor(Math.Sqrt(Math.Max(0d, (radius * radius) - (dy * dy))));
        return new SD.Rectangle((client.Width / 2) - half, top, Math.Max(1, half * 2), height);
    }

    /// <summary>One tween per second drives the ring's sweep and the digit's landing. It is
    /// restarted by the tick, so it can never drift away from the clock that actually owns
    /// the delay.</summary>
    private void RunSecond()
    {
        _second?.Dispose();
        _secondFraction = 0;
        _second = Motion.Run(
            1000,
            Motion.Linear,
            amount =>
            {
                // With animations off Motion.Run lands 1.0 inline, which parked the ring a
                // whole second ahead of the digit inside it — a 3-second countdown showed a
                // third already consumed on its first frame. The step belongs at the START of
                // the second it describes.
                _secondFraction = Motion.AnimationsEnabled() ? amount : 0;
                if (!IsDisposed)
                    Invalidate();
            },
            () => _second = null);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            _second?.Dispose();
            _second = null;
            if (!_done)
            {
                _done = true;
                DialogResult = WF.DialogResult.OK;
            }
            return;
        }

        RunSecond();
        Invalidate();
    }

    private void CancelCountdown()
    {
        if (_done)
            return;

        _done = true;
        _timer.Stop();
        _second?.Dispose();
        _second = null;
        DialogResult = WF.DialogResult.Cancel;
    }

    private void PositionOverRegion()
    {
        try
        {
            int x = _regionPx.X + (_regionPx.Width - Width) / 2;
            int y = _regionPx.Y + (_regionPx.Height - Height) / 2;
            SetWindowPos(Handle, HwndTopmost, x, y, Width, Height, 0);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to position fast countdown window", ex);
        }
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        IntPtr regionHandle = CreateEllipticRgn(0, 0, Width + 1, Height + 1);
        Region = SD.Region.FromHrgn(regionHandle);
        DeleteObject(regionHandle);
    }

    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
