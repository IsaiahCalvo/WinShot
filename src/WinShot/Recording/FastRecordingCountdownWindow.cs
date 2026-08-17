using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastRecordingCountdownWindow : WF.Form
{
    // Design 4d: charcoal disc, white progress ring on a faint track, muted hint.
    private static readonly SD.Color Back = SD.Color.FromArgb(0x1C, 0x1C, 0x1E);
    private static readonly SD.Color RingTrack = SD.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
    private static readonly SD.Color RingProgress = ThemePalette.ActionBg;
    private static readonly SD.Color TextColor = ThemePalette.TextPrimary;
    private static readonly SD.Color MutedText = ThemePalette.TextMuted;

    private readonly WF.Timer _timer = new() { Interval = 1000 };
    private readonly SD.Rectangle _regionPx;
    private readonly int _total;
    private int _remaining;
    private bool _done;

    public FastRecordingCountdownWindow(int seconds, SD.Rectangle regionScreenPx)
    {
        _remaining = Math.Max(1, seconds);
        _total = _remaining;
        _regionPx = regionScreenPx;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        ClientSize = new SD.Size(150, 150);
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

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Back);

        var ringRect = new SD.RectangleF(2.5f, 2.5f, Width - 6f, Height - 6f);
        using (var track = new SD.Pen(RingTrack, 3))
            e.Graphics.DrawEllipse(track, ringRect);
        // Progress ring: sweeps down as the countdown runs (design 4d).
        float fraction = Math.Clamp(_remaining / (float)_total, 0f, 1f);
        if (fraction > 0)
        {
            using var progress = new SD.Pen(RingProgress, 3)
            {
                StartCap = SD.Drawing2D.LineCap.Round,
                EndCap = SD.Drawing2D.LineCap.Round,
            };
            e.Graphics.DrawArc(progress, ringRect, -90f, 360f * fraction);
        }

        using var countFont = ThemePalette.UiFontSemiBold(58f, SD.GraphicsUnit.Pixel);
        using var hintFont = ThemePalette.UiFont(8f);
        var countText = _remaining.ToString();
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(
            e.Graphics,
            countText,
            countFont,
            new SD.Rectangle(0, 22, Width, 84),
            TextColor,
            flags);
        WF.TextRenderer.DrawText(
            e.Graphics,
            "Esc to cancel",
            hintFont,
            new SD.Rectangle(0, Height - 46, Width, 18),
            MutedText,
            flags);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            if (!_done)
            {
                _done = true;
                DialogResult = WF.DialogResult.OK;
            }
            return;
        }

        Invalidate();
    }

    private void CancelCountdown()
    {
        if (_done)
            return;

        _done = true;
        _timer.Stop();
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
