using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Cursors the selectors swap in to say what a click will do. Drawn at runtime, so the repo
/// carries no .cur assets and the mark tracks the app's own palette.
/// </summary>
internal static class SelectorCursors
{
    /// <summary>One cursor per monitor scale, not one per process. A custom HCURSOR is drawn
    /// by Windows at its native size in PHYSICAL pixels — unlike the system cursors it stands
    /// in for, it is never scaled for the monitor it lands on — so a single 32x32 build read
    /// two-thirds size against a 48 px arrow at 150% and half size at 200%.</summary>
    private static readonly Dictionary<int, WF.Cursor> _cameras = [];

    /// <summary>Logical size of the camera cursor, before the monitor scale.</summary>
    private const int CameraLogicalPx = 32;

    /// <summary>
    /// The window-pick cursor: a camera, so "click here" reads as "photograph this window"
    /// rather than the generic hand a link would use. <paramref name="scale"/> is the
    /// selector monitor's DPI factor (RecordingMonitorDpi.ScaleFor) — AutoScaleMode.Dpi never
    /// scales anything here, so every surface multiplies by hand.
    /// </summary>
    public static WF.Cursor Camera(double scale)
    {
        int key = Math.Clamp((int)Math.Round(scale * 100), 100, 400);
        lock (_cameras)
        {
            if (_cameras.TryGetValue(key, out var cached))
                return cached;
            WF.Cursor built = BuildCamera(key / 100.0);
            _cameras[key] = built;
            return built;
        }
    }

    private static WF.Cursor BuildCamera(double scale)
    {
        try
        {
            using SD.Bitmap bmp = BuildCameraBitmap(scale);
            // GetHicon hands back a handle the caller owns, and Cursor(IntPtr) does not take
            // ownership — so this handle lives until the process does. That is deliberate,
            // not a leak that grows: the cache key is the clamped scale, so a run holds one
            // HICON per distinct monitor scale (100/125/150/175/200 in practice) and every
            // one of them is still in use by a live Cursor. Destroying it here would blank
            // the cursor; there is nowhere later to destroy it from.
            return new WF.Cursor(bmp.GetHicon());
        }
        catch (Exception ex)
        {
            Log.Error("Camera cursor could not be built; using the hand cursor", ex);
            return WF.Cursors.Hand;
        }
    }

    // The camera on its own design grid: 24 wide by 18 tall, body from y=4 down with the
    // viewfinder hump standing on it, and the lens in the middle of the body.
    private const float GridW = 24f;
    private const float GridH = 18f;
    private const float LensCx = 12f;
    private const float LensCy = 11.2f;
    private const float LensR = 4.2f;

    /// <summary>
    /// The cursor image before it becomes an HCURSOR, exposed so a test can read its pixels.
    ///
    /// Drawn from a PATH, not from the icon font. Segoe Fluent's Camera (U+E722) is a stroked
    /// outline glyph: painted white with a dark halo it is a hollow wireframe, and over a busy
    /// window the reporter could not see where the camera was — "it's just an outline". There
    /// is no filled twin of that codepoint to swap in, and asking DrawString for a fill only
    /// fills the strokes. A silhouette solves it outright, and it also retires the whole
    /// point-font-versus-process-DPI trap that clipped this cursor at every scale: geometry
    /// here is device pixels the whole way down, so what fits is arithmetic rather than a
    /// property of an installed font.
    ///
    /// Dark body (<see cref="ThemePalette.ToolbarBg"/>) inside a white contour, so it reads
    /// both ways round: the fill carries it over a light window, the contour over a dark one.
    /// </summary>
    internal static SD.Bitmap BuildCameraBitmap(double scale)
    {
        int size = WinShot.Recording.RecordingMonitorDpi.Px(CameraLogicalPx, scale);
        // The contour, in whole device pixels — a sub-pixel pen on a 150 % display reads as a
        // blur rather than an edge, the same rounding the selector's guide lines take.
        float stroke = Math.Max(2f, (float)Math.Round(2d * scale));

        // Fit the grid to the bitmap with the contour's own width counted in, and a pixel of
        // margin so no ink lands on the outermost row or column. Solving for the wider axis is
        // enough: the grid is wider than it is tall. Ink is then exactly
        // (24k + stroke) x (18k + stroke), which is what centres it — the old build measured
        // a font's ink with a throwaway render because it could not know it any other way.
        // Two pixels of margin per side, not one: the contour's outer edge lands on an exact
        // pixel boundary and antialiasing still tints the column past it, so a one-pixel
        // budget put ink in the last column at every scale.
        float k = (size - 4 - stroke) / GridW;
        float originX = ((size - ((GridW * k) + stroke)) / 2f) + (stroke / 2f);
        float originY = ((size - ((GridH * k) + stroke)) / 2f) + (stroke / 2f);

        var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var g = SD.Graphics.FromImage(bmp);
            g.Clear(SD.Color.Transparent);
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;

            using var silhouette = CameraSilhouette();
            using var toDevice = new SD.Drawing2D.Matrix(k, 0f, 0f, k, originX, originY);
            silhouette.Transform(toDevice);

            // Fill first, contour second: the pen straddles the edge, so stroking afterwards
            // puts white on both sides of it and the dark body never bleeds through it.
            using (var body = new SD.SolidBrush(SD.Color.FromArgb(255, ThemePalette.ToolbarBg)))
                g.FillPath(body, silhouette);
            using (var contour = new SD.Pen(SD.Color.White, stroke)
            {
                LineJoin = SD.Drawing2D.LineJoin.Round,
            })
                g.DrawPath(contour, silhouette);

            // The lens: a white ring on the dark body, which is what makes the silhouette read
            // as a camera rather than a rounded box with a tab.
            using (var lensPath = new SD.Drawing2D.GraphicsPath())
            {
                lensPath.AddEllipse(LensCx - LensR, LensCy - LensR, LensR * 2f, LensR * 2f);
                lensPath.Transform(toDevice);
                using var lens = new SD.Pen(SD.Color.White, Math.Max(1.5f, stroke * 0.75f));
                g.DrawPath(lens, lensPath);
            }

            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One closed contour on the 24x18 grid — body and viewfinder hump in a single figure, not
    /// two overlapping rectangles. Two figures would be a union for the FILL and two separate
    /// outlines for the STROKE, which draws a white line across the body where the hump sits
    /// on it.
    /// </summary>
    private static SD.Drawing2D.GraphicsPath CameraSilhouette()
    {
        var path = new SD.Drawing2D.GraphicsPath();
        path.AddArc(0f, 4f, 6f, 6f, 180f, 90f);     // body top-left, ends (3,4)
        path.AddLine(3f, 4f, 5f, 4f);               // body top edge up to the hump
        path.AddLine(5f, 4f, 5f, 1.5f);             // hump left side
        path.AddArc(5f, 0f, 3f, 3f, 180f, 90f);     // hump top-left, ends (6.5,0)
        path.AddArc(9f, 0f, 3f, 3f, 270f, 90f);     // hump top-right, ends (12,1.5)
        path.AddLine(12f, 1.5f, 12f, 4f);           // hump right side
        path.AddLine(12f, 4f, 21f, 4f);             // body top edge, rest of it
        path.AddArc(18f, 4f, 6f, 6f, 270f, 90f);    // body top-right
        path.AddArc(18f, 12f, 6f, 6f, 0f, 90f);     // body bottom-right
        path.AddArc(0f, 12f, 6f, 6f, 90f, 90f);     // body bottom-left, ends (0,15)
        path.CloseFigure();
        return path;
    }
}
