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

/// <summary>
/// Dims only pixels outside the recording rectangle. Because the windows never overlap the
/// captured rectangle, recording remains clean even when display-affinity exclusion is not
/// available. Windows are click-through and exist only for the active recording.
/// </summary>
public sealed class FastRecordingDimOverlay : IDisposable
{
    private readonly List<DimSurface> _surfaces = new();

    public FastRecordingDimOverlay(SD.Rectangle recordingArea)
    {
        foreach (WF.Screen screen in WF.Screen.AllScreens)
        {
            foreach (SD.Rectangle rect in RecordingDimOverlayLayout.OutsideRecordingArea(screen.Bounds, recordingArea))
                _surfaces.Add(new DimSurface(rect));
        }
    }

    public void Show()
    {
        foreach (DimSurface surface in _surfaces)
            surface.Show();
    }

    public void Dispose()
    {
        foreach (DimSurface surface in _surfaces)
        {
            try { surface.Close(); surface.Dispose(); }
            catch (Exception ex) { Log.Error("Failed to close recording dim surface", ex); }
        }
        _surfaces.Clear();
    }

    private sealed class DimSurface : WF.Form
    {
        public DimSurface(SD.Rectangle bounds)
        {
            AutoScaleMode = WF.AutoScaleMode.None;
            BackColor = SD.Color.Black;
            Bounds = bounds;
            FormBorderStyle = WF.FormBorderStyle.None;
            Opacity = 0.36;
            ShowInTaskbar = false;
            StartPosition = WF.FormStartPosition.Manual;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation => true;

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
