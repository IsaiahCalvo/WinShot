using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

public sealed class FastSelfTimerWindow : WF.Form
{
    private static readonly SD.Color Back = SD.Color.FromArgb(30, 30, 30);
    private static readonly SD.Color Border = SD.Color.FromArgb(54, 255, 255, 255);
    private static readonly SD.Color TextColor = SD.Color.White;

    private readonly WF.Timer _timer = new() { Interval = 1000 };
    private int _remaining;

    public FastSelfTimerWindow(int seconds)
    {
        _remaining = SelfTimerOptions.ClampDelaySeconds(seconds);

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        ClientSize = new SD.Size(150, 150);
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
                Close();
            }
            else
            {
                Invalidate();
            }
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

        using var pen = new SD.Pen(Border, 1);
        e.Graphics.DrawEllipse(pen, 1, 1, Width - 3, Height - 3);

        using var font = new SD.Font("Segoe UI Semibold", 72f, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.NoPadding;
        WF.TextRenderer.DrawText(
            e.Graphics,
            _remaining.ToString(),
            font,
            new SD.Rectangle(0, 0, Width, Height - 6),
            TextColor,
            flags);
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
