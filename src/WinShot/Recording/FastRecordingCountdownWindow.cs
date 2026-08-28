using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastRecordingCountdownWindow : WF.Form
{
    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private static readonly SD.Color Ring = ThemePalette.Accent;
    private static readonly SD.Color TextColor = SD.Color.White;
    // Light enough to pass 4.5:1 on the #2B2B2B card (136 gray failed contrast).
    private static readonly SD.Color MutedText = SD.Color.FromArgb(170, 170, 170);

    private readonly WF.Timer _timer = new() { Interval = 1000 };
    private readonly SD.Rectangle _regionPx;
    private int _remaining;
    private bool _done;

    // Scales the badge (and its pixel-unit digits) to the recorded region's monitor,
    // so it reads the same physical size on 100% and 150% displays.
    private readonly double _scale;

    private int S(int logical) => (int)Math.Round(logical * _scale);

    public FastRecordingCountdownWindow(int seconds, SD.Rectangle regionScreenPx)
    {
        _remaining = Math.Max(1, seconds);
        _regionPx = regionScreenPx;
        _scale = RecordingMonitorDpi.ScaleFor(regionScreenPx);

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

        using var ring = new SD.Pen(Ring, Math.Max(3, S(3)));
        e.Graphics.DrawEllipse(ring, 2, 2, Width - 5, Height - 5);

        // Real bold face, not faux-bold synthesized over Semibold.
        using var countFont = new SD.Font("Segoe UI", S(84), SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
        using var hintFont = new SD.Font("Segoe UI", 11f, SD.FontStyle.Regular, SD.GraphicsUnit.Point);
        var countText = _remaining.ToString();
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(
            e.Graphics,
            countText,
            countFont,
            new SD.Rectangle(0, S(10), Width, S(120)),
            TextColor,
            flags);
        WF.TextRenderer.DrawText(
            e.Graphics,
            "Click or Esc to cancel",
            hintFont,
            new SD.Rectangle(0, Height - S(42), Width, S(24)),
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
