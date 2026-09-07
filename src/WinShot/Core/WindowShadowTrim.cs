using System.Runtime.InteropServices;
using SD = System.Drawing;
using SDD = System.Drawing.Drawing2D;

namespace WinShot.Core;

/// <summary>
/// Cuts the shadow out of the corners of a window capture.
/// </summary>
/// <remarks>
/// Along a window's edges there is nothing to trim on Windows: the capture rect is DWM's
/// extended frame bounds, which already exclude the system drop shadow. The corners are the
/// exception, and they are the whole of it. Windows 11 rounds a framed window while the rect
/// around it stays square, so the four wedges outside the corner arcs are not the window —
/// they are whatever sat behind it, darkened by the shadow DWM draws underneath. That grey
/// smudge is the window shadow a WinShot capture actually carries, and cutting the wedges
/// away is what trimming it means here.
///
/// Deliberately headless GDI+, like <see cref="ScreenshotBackdrop"/>: it runs on the capture
/// path with no window and no user interaction, and it returns a new bitmap rather than
/// mutating the capture, so a failure costs the trim and never the shot.
/// </remarks>
public static class WindowShadowTrim
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRoundSmall = 3;

    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;

    /// <summary>Windows 11's corner radius in DIPs — 8 for an ordinary window, 4 for one that
    /// asked for the small round. Scaled to the window's own DPI before it is used.</summary>
    internal const int RoundRadiusDip = 8;
    internal const int RoundSmallRadiusDip = 4;

    /// <summary>
    /// The corner radius of <paramref name="hwnd"/> in physical pixels, or 0 when its corners
    /// are square and there is nothing to trim: a maximized window, a frameless one, a window
    /// that opted out, or any window on a DWM too old to have a corner preference at all —
    /// which is also every Windows 10 window, none of which is rounded.
    /// </summary>
    public static int RadiusFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return 0;

        // Pre-Windows-11 DWM rejects the attribute outright. Letting the OS answer that is
        // cheaper and truer than a build-number check.
        if (DwmGetWindowAttribute(hwnd, DwmwaWindowCornerPreference, out int preference, sizeof(int)) != 0)
            return 0;
        if (preference == DwmwcpDoNotRound)
            return 0;

        if (IsZoomed(hwnd))
            return 0;
        // DWMWCP_DEFAULT means "the system decides", and what it decides on is the frame:
        // a captioned window is rounded, a bare popup is not.
        if ((GetWindowLongPtr(hwnd, GwlStyle).ToInt64() & WsCaption) != WsCaption)
            return 0;

        int dip = preference == DwmwcpRoundSmall ? RoundSmallRadiusDip : RoundRadiusDip;
        int dpi = GetDpiForWindow(hwnd);
        if (dpi <= 0) dpi = 96;
        return Math.Max(1, (int)Math.Round(dip * dpi / 96.0));
    }

    /// <summary>The capture with <paramref name="hwnd"/>'s rounded corners cut out, or null
    /// when that window's corners are square.</summary>
    public static SD.Bitmap? Trim(SD.Bitmap shot, IntPtr hwnd) => Trim(shot, RadiusFor(hwnd));

    /// <summary>
    /// The capture with corners of <paramref name="radius"/> pixels cut to transparent, or
    /// null when there is nothing to do — a radius of 0, or a capture too small to round
    /// without eating its own middle. Never disposes or mutates <paramref name="shot"/>.
    /// </summary>
    public static SD.Bitmap? Trim(SD.Bitmap shot, int radius)
    {
        if (radius <= 0 || shot.Width < radius * 2 || shot.Height < radius * 2)
            return null;

        SD.Bitmap? rounded = null;
        try
        {
            rounded = new SD.Bitmap(shot.Width, shot.Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using (SD.Graphics g = SD.Graphics.FromImage(rounded))
            {
                // Painting the shot *through* the rounded path onto an empty canvas is what
                // gives the arc a soft edge: filling a path antialiases its boundary into
                // alpha, where clipping and erasing would leave a staircase.
                g.SmoothingMode = SDD.SmoothingMode.AntiAlias;
                using var path = GdiPaths.RoundedRect(new SD.Rectangle(0, 0, shot.Width, shot.Height), radius);
                // GDI+ centres pixel (0,0) on the origin, so a path drawn from 0 to w covers
                // only half of the first row and column — the four straight edges of the
                // window would come out faded. Nudging the path (not the graphics, which
                // would drag the texture's sampling with it) puts the outer rows back inside.
                using (var half = new SDD.Matrix())
                {
                    half.Translate(-0.5f, -0.5f);
                    path.Transform(half);
                }
                using var brush = new SD.TextureBrush(shot);
                g.FillPath(brush, path);
            }
            return rounded;
        }
        catch (Exception ex)
        {
            // Trimming is tidying. Losing it must never lose the capture.
            rounded?.Dispose();
            Log.Error("Window shadow trim failed; keeping the untrimmed capture", ex);
            return null;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
