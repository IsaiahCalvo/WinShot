using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>
/// Ground truth for "which part of this region actually scrolls": grab two frames with no
/// input (whatever changes by itself is animation, not scrolling — mask it), inject ONE
/// wheel notch, grab again, and take the bounding rectangle of the pixels that moved. The
/// notch is then reversed. Detector hints (UIA/Win32) only snap the result's edges; when
/// they disagree with the probe, the probe wins. Any failure returns the original region —
/// the probe may narrow a capture, never block one.
/// </summary>
public static class ScrollProbe
{
    private const int SampleStep = 2;
    private const int Tolerance = 12;
    private const int MinChangedSamplesPerLine = 4;
    private const int SnapPx = 12;
    private const int MinPaneWidth = 220;
    private const int MinPaneHeight = 160;

    public static SD.Rectangle Refine(
        SD.Rectangle regionPx,
        PaneInfo? hint,
        Func<SD.Rectangle, SD.Bitmap>? grab = null,
        Action<int>? wheel = null,
        Action<int>? sleep = null)
    {
        grab ??= CaptureService.CaptureScreenRegion;
        wheel ??= WheelAtRegionCenter(regionPx);
        sleep ??= Thread.Sleep;
        SD.Bitmap? before = null, control = null, after = null;
        bool scrolled = false;
        try
        {
            control = grab(regionPx);
            sleep(140);
            before = grab(regionPx);
            var animation = ChangedLines(control, before);

            wheel(-120);
            scrolled = true;
            sleep(260); // smooth-scroll settle; a second grab below absorbs stragglers
            after = grab(regionPx);
            sleep(160);
            using (SD.Bitmap settled = grab(regionPx))
            {
                if (!ImageStitcher.FramesIdentical(after, settled))
                {
                    after.Dispose();
                    after = (SD.Bitmap)settled.Clone();
                }
            }

            var moved = ChangedLines(before, after);
            SD.Rectangle raw = BoundingRect(moved, animation, regionPx);
            if (raw.IsEmpty)
            {
                Log.Info("Scroll probe: nothing moved — keeping the selected region");
                return regionPx;
            }

            SD.Rectangle movedRect = FinishRect(raw, hint, regionPx);
            if (movedRect.Width < MinPaneWidth || movedRect.Height < MinPaneHeight ||
                movedRect.Width * movedRect.Height >= regionPx.Width * regionPx.Height * 92L / 100L)
            {
                return regionPx; // nothing meaningful to shrink (or implausibly tiny)
            }
            Log.Info($"Scroll probe: region {regionPx.Width}x{regionPx.Height} → pane " +
                $"{movedRect.Width}x{movedRect.Height} at ({movedRect.X},{movedRect.Y}) " +
                $"hint={hint?.Source.ToString() ?? "none"}");
            return movedRect;
        }
        catch (Exception ex)
        {
            Log.Error("Scroll probe failed (non-fatal) — keeping the selected region", ex);
            return regionPx;
        }
        finally
        {
            if (scrolled)
            {
                try
                {
                    wheel(+120); // put the page back where the user left it
                    sleep(200);
                }
                catch (Exception) { /* restore is best-effort */ }
            }
            control?.Dispose();
            before?.Dispose();
            after?.Dispose();
        }
    }

    /// <summary>Mirrors the capture loop's wheel routing: teleport the cursor to the region
    /// center for the tick (wheel follows the cursor), then put it back.</summary>
    private static Action<int> WheelAtRegionCenter(SD.Rectangle region) => delta =>
    {
        int cx = region.X + region.Width / 2, cy = region.Y + region.Height / 2;
        GetCursorPos(out var orig);
        SetCursorPos(cx, cy);
        SendWheel(delta);
        if (GetCursorPos(out var now) && now.X == cx && now.Y == cy)
            SetCursorPos(orig.X, orig.Y);
    };

    private readonly record struct Changed(bool[] Columns, bool[] Rows);

    /// <summary>Per-column and per-row "did anything change here" flags between two frames,
    /// sampled every <see cref="SampleStep"/> px with a small luma tolerance.</summary>
    private static Changed ChangedLines(SD.Bitmap a, SD.Bitmap b)
    {
        int width = a.Width, height = a.Height;
        var colHits = new int[width];
        var rowHits = new int[height];
        var da = a.LockBits(new SD.Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(new SD.Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowA = new byte[width * 4];
            var rowB = new byte[width * 4];
            for (int y = 0; y < height; y += SampleStep)
            {
                Marshal.Copy(da.Scan0 + y * da.Stride, rowA, 0, rowA.Length);
                Marshal.Copy(db.Scan0 + y * db.Stride, rowB, 0, rowB.Length);
                for (int x = 0; x < width; x += SampleStep)
                {
                    int i = x * 4;
                    if (Math.Abs(rowA[i] - rowB[i]) > Tolerance ||
                        Math.Abs(rowA[i + 1] - rowB[i + 1]) > Tolerance ||
                        Math.Abs(rowA[i + 2] - rowB[i + 2]) > Tolerance)
                    {
                        colHits[x]++;
                        rowHits[y]++;
                    }
                }
            }
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }

        var cols = new bool[width];
        var rows = new bool[height];
        for (int x = 0; x < width; x++) cols[x] = colHits[x] >= MinChangedSamplesPerLine;
        for (int y = 0; y < height; y++) rows[y] = rowHits[y] >= MinChangedSamplesPerLine;
        return new Changed(cols, rows);
    }

    private static SD.Rectangle BoundingRect(Changed moved, Changed animation, SD.Rectangle region)
    {
        int left = -1, right = -1, top = -1, bottom = -1;
        for (int x = 0; x < moved.Columns.Length; x++)
        {
            if (!moved.Columns[x] || animation.Columns[x]) continue;
            if (left < 0) left = x;
            right = x;
        }
        for (int y = 0; y < moved.Rows.Length; y++)
        {
            if (!moved.Rows[y] || animation.Rows[y]) continue;
            if (top < 0) top = y;
            bottom = y;
        }
        if (left < 0 || top < 0)
            return SD.Rectangle.Empty;
        return new SD.Rectangle(region.X + left, region.Y + top, right - left + 1, bottom - top + 1);
    }

    private const int Pad = 16;

    /// <summary>
    /// The probe sees moved INK, not the pane's blank padding, so the hint decides each
    /// edge unless the movement clearly escapes it (then the hint lied for that edge and
    /// the probe's padded edge wins). Without a hint, breathe by <see cref="Pad"/> px so
    /// text is not cropped flush.
    /// </summary>
    private static SD.Rectangle FinishRect(SD.Rectangle raw, PaneInfo? hint, SD.Rectangle region)
    {
        SD.Rectangle h = hint is PaneInfo info
            ? SD.Rectangle.Intersect(info.RectPx, region)
            : SD.Rectangle.Empty;
        SD.Rectangle result;
        if (h.IsEmpty)
        {
            result = raw;
            result.Inflate(Pad, Pad);
        }
        else
        {
            result = SD.Rectangle.FromLTRB(
                raw.Left < h.Left - SnapPx ? raw.Left - Pad : h.Left,
                raw.Top < h.Top - SnapPx ? raw.Top - Pad : h.Top,
                raw.Right > h.Right + SnapPx ? raw.Right + Pad : h.Right,
                raw.Bottom > h.Bottom + SnapPx ? raw.Bottom + Pad : h.Bottom);
        }
        result.Intersect(region);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private static void SendWheel(int delta)
    {
        var input = new Input
        {
            type = 0,
            mi = new MouseInput { mouseData = unchecked((uint)delta), dwFlags = 0x0800 /* WHEEL */ },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        private uint _pad; // x64 INPUT aligns the union at offset 8 (it contains a pointer)
        public MouseInput mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
