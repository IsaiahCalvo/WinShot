using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// Draws the live mouse cursor into a captured bitmap. The screen-capture APIs WinShot uses
/// never include it, so "Show cursor on screenshots" was a stored setting that did nothing —
/// and a screenshot meant to point at something had nothing pointing at it.
/// </summary>
internal static class CursorOverlay
{
    /// <summary>
    /// Composites the current cursor onto <paramref name="target"/>, positioned as if
    /// <paramref name="screenRect"/> were the captured area in physical screen pixels. Silent
    /// and best-effort: a missing or hidden cursor is a no-op, and no failure here may cost
    /// the user their capture.
    /// </summary>
    public static void Draw(SD.Bitmap target, SD.Rectangle screenRect)
    {
        ArgumentNullException.ThrowIfNull(target);
        IntPtr icon = IntPtr.Zero;
        try
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref info) || info.flags != CursorShowing || info.hCursor == IntPtr.Zero)
                return;

            // Copy first: the cursor handle is shared, and it can be destroyed out from
            // under a draw when the pointer changes shape mid-capture.
            icon = CopyIcon(info.hCursor);
            if (icon == IntPtr.Zero)
                return;

            SD.Point hotspot = HotspotOf(icon);
            int x = info.ptScreenPos.X - screenRect.X - hotspot.X;
            int y = info.ptScreenPos.Y - screenRect.Y - hotspot.Y;

            using SD.Graphics g = SD.Graphics.FromImage(target);
            IntPtr hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, x, y, icon, 0, 0, 0, IntPtr.Zero, DiNormal);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Drawing the cursor into the capture failed; the capture is unchanged", ex);
        }
        finally
        {
            if (icon != IntPtr.Zero)
                DestroyIcon(icon);
        }
    }

    /// <summary>The cursor's hotspot, or (0,0) when Windows will not say.</summary>
    private static SD.Point HotspotOf(IntPtr icon)
    {
        if (!GetIconInfo(icon, out ICONINFO info))
            return SD.Point.Empty;
        try
        {
            return new SD.Point(info.xHotspot, info.yHotspot);
        }
        finally
        {
            // GetIconInfo hands back two bitmaps the caller owns; leaking them per capture
            // is a GDI handle leak on a path that runs on every screenshot.
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
    }

    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
