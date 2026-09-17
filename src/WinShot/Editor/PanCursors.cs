using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Editor;

/// <summary>
/// The editor's pan cursors — Survey's CSS <c>grab</c> / <c>grabbing</c> pair (PDFViewer.jsx),
/// which Windows does not ship. Drawn at runtime like <see cref="Capture.SelectorCursors"/>,
/// so the repo carries no .cur assets and the hand is built at the monitor's own scale: a
/// custom HCURSOR is shown at its physical size, never scaled, so a fixed 32 px file read
/// two-thirds size beside a 48 px arrow at 150 %.
/// </summary>
internal static class PanCursors
{
    private static readonly Dictionary<(int Scale, bool Closed), Cursor> _cache = [];

    /// <summary>Logical size of the hand, before the monitor scale.</summary>
    private const int LogicalPx = 32;

    /// <summary>Open hand: "you can drag this".</summary>
    public static Cursor Grab(double scale) => Get(scale, closed: false);

    /// <summary>Closed hand: "you are dragging this".</summary>
    public static Cursor Grabbing(double scale) => Get(scale, closed: true);

    private static Cursor Get(double scale, bool closed)
    {
        int key = Math.Clamp((int)Math.Round(scale * 100), 100, 400);
        lock (_cache)
        {
            if (_cache.TryGetValue((key, closed), out var cached))
                return cached;
            Cursor built = Build(key / 100.0, closed);
            _cache[(key, closed)] = built;
            return built;
        }
    }

    private static Cursor Build(double scale, bool closed)
    {
        try
        {
            using SD.Bitmap bmp = BuildHandBitmap(scale, closed);
            // The HICON lives for the process, on purpose: one per (scale, state) and every
            // one is held by a live Cursor. See SelectorCursors.BuildCamera for the reasoning.
            return CursorInteropHelper.Create(new SafeFileHandle(bmp.GetHicon(), ownsHandle: false));
        }
        catch (Exception ex)
        {
            Log.Error("Pan cursor could not be built; using the system cursor", ex);
            return closed ? Cursors.SizeAll : Cursors.Hand;
        }
    }

    /// <summary>
    /// A white hand with a dark contour on a 32-unit grid, open or as a fist, scaled to
    /// <paramref name="scale"/>. Internal so the render tests can scan the ink.
    /// </summary>
    internal static SD.Bitmap BuildHandBitmap(double scale, bool closed)
    {
        int size = WinShot.Recording.RecordingMonitorDpi.Px(LogicalPx, scale);
        float k = (float)(size / (double)LogicalPx);
        // Whole device pixels for the contour, like the camera: a sub-pixel pen blurs.
        float stroke = Math.Max(2f, (float)Math.Round(2d * scale));

        var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var g = SD.Graphics.FromImage(bmp);
            g.Clear(SD.Color.Transparent);
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new SD.SolidBrush(SD.Color.White);
            using var contour = new SD.Pen(SD.Color.FromArgb(255, ThemePalette.ToolbarBg), stroke)
            {
                LineJoin = SD.Drawing2D.LineJoin.Round,
            };

            // The grid keeps 3 units clear of every edge, so no ink lands on the outermost pixel.
            using var toDevice = new SD.Drawing2D.Matrix(k, 0f, 0f, k, 0f, 0f);

            foreach (var (x0, y0, x1, y1, r) in closed ? Fist() : OpenHand())
            {
                using var part = RoundedRect(x0, y0, x1, y1, r);
                part.Transform(toDevice);
                // Fill first, contour second, each part on top of the last: the pen straddles
                // the edge, so the later part's white covers the earlier part's line where they
                // overlap and the hand reads as one shape, not a stack of capsules.
                g.FillPath(fill, part);
                g.DrawPath(contour, part);
            }
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    // Capsules on the 32-grid: (left, top, right, bottom, corner radius). Drawn in order, so
    // the palm goes last and sits over the finger roots.
    private static IEnumerable<(float, float, float, float, float)> OpenHand()
    {
        yield return (4f, 15f, 12f, 24f, 4f);            // thumb
        foreach (var (x, top) in new[] { (12f, 6f), (17f, 4f), (22f, 5f), (26.5f, 8f) })
            yield return (x - 2.5f, top, x + 2.5f, 22f, 2.5f); // four fingers
        yield return (9f, 14f, 29f, 29f, 6f);             // palm
    }

    private static IEnumerable<(float, float, float, float, float)> Fist()
    {
        foreach (var (x, top) in new[] { (12f, 10f), (17f, 9f), (22f, 10f), (26.5f, 12f) })
            yield return (x - 2.5f, top, x + 2.5f, 20f, 2.5f); // curled fingers
        yield return (9f, 15f, 29f, 29f, 6f);             // palm
        yield return (6f, 16f, 17f, 23f, 3.5f);           // thumb folded across
    }

    private static SD.Drawing2D.GraphicsPath RoundedRect(float x0, float y0, float x1, float y1, float r)
    {
        var path = new SD.Drawing2D.GraphicsPath();
        float d = r * 2f;
        path.AddArc(x0, y0, d, d, 180f, 90f);
        path.AddArc(x1 - d, y0, d, d, 270f, 90f);
        path.AddArc(x1 - d, y1 - d, d, d, 0f, 90f);
        path.AddArc(x0, y1 - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }
}
