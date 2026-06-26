using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>
/// Position-aware stitch accumulator for MANUAL scrolling capture. Unlike append-only stitching,
/// every frame is located by where its rows match the WHOLE canvas — so scrolling up works as well
/// as down, re-scrolling a section overwrites in place, and a section skipped by a fast scroll is
/// filled the moment the user scrolls back over it.
///
/// Coordinates: canvas row index == vertical position. The first frame sits at row 0; later frames
/// extend the canvas up (prepend) or down (append). Matching ignores un-filled rows, so a gap never
/// anchors a frame.
///
/// Flick handling (the hard case): when a frame has NO overlap with the canvas (a flick past a full
/// frame), its true distance is unknown, so committing it to the main canvas would compress the gap
/// and corrupt later fills. Instead it goes to a FLOATING sub-canvas that grows on its own. The
/// moment one frame matches BOTH the floating segment and the main canvas, the real offset is known
/// and the floating segment is merged into the main canvas at its true position — closing the gap
/// exactly. If capture ends with the floating segment never bridged, it's appended below a visible
/// marker band (its true position genuinely unknowable without overlap).
///
/// Not thread-safe; the capture loop owns it on one thread. No UI dependencies — unit-testable.
/// </summary>
public sealed class ScrollCanvas : IDisposable
{
    /// <summary>Minimum run of matching rows (containing a distinctive row) to trust a placement.</summary>
    private const int MinRun = 24;

    /// <summary>Height of the marker band shown where a flick segment never reconnected — a fixed
    /// "content skipped here" indicator, not the true (unknowable) gap size.</summary>
    private const int MarkerGapPx = 22;

    private const int MaxHeight = 32000;

    // A muted amber band reads as "skipped / not real content" without being a screaming color.
    private static readonly SD.Color MarkerColor = SD.Color.FromArgb(255, 90, 74, 42);

    private SD.Bitmap? _img;
    private readonly List<ulong> _hash = new();
    private readonly List<bool> _filled = new();
    private int _lastTop;   // canvas row where the most recent frame was placed
    private int _width;

    private ScrollCanvas? _floating; // disconnected segment awaiting a bridge frame to merge

    public int Height => (_img?.Height ?? 0) + (_floating is null ? 0 : MarkerGapPx + _floating.Height);

    /// <summary>True while content is skipped and not yet stitched back (a floating segment exists).</summary>
    public bool HasGap => _floating is not null;

    /// <summary>What happened when a frame was placed — drives the live "too fast" warning + preview.</summary>
    public readonly record struct PlaceResult(bool Moved, bool Disconnected, bool HasGap);

    /// <summary>Locates <paramref name="frame"/> and writes it where it belongs, growing/filling/merging
    /// as needed. <see cref="PlaceResult.Disconnected"/> is true on the frame that opens a gap.</summary>
    public PlaceResult Place(SD.Bitmap frame)
    {
        ulong[] fh = ImageStitcher.RowHashes(frame);
        if (_img is null)
        {
            Seed(frame, fh);
            return new PlaceResult(true, false, false);
        }
        if (frame.Width != _width)
            return new PlaceResult(false, false, HasGap); // region resized mid-capture: ignore

        return _floating is null ? PlaceIntoMain(frame, fh) : PlaceWhileFloating(frame, fh);
    }

    private PlaceResult PlaceIntoMain(SD.Bitmap frame, ulong[] fh)
    {
        if (IsUnchangedAt(this, fh, _lastTop))
            return new PlaceResult(false, false, false); // paused

        int fhLen = fh.Length;
        // Windowed first (continuous scroll up/down), then global (a jump that still overlaps content).
        int? p = FindPlacement(fh, _lastTop - fhLen, _lastTop + fhLen) ?? FindPlacement(fh, int.MinValue, int.MaxValue);
        if (p is int placement)
        {
            Apply(frame, fh, placement);
            return new PlaceResult(true, false, false);
        }

        // No overlap anywhere: a hard flick onto disconnected content. Park it in a floating segment.
        _floating = new ScrollCanvas();
        _floating.Seed(frame, fh);
        return new PlaceResult(true, true, true);
    }

    private PlaceResult PlaceWhileFloating(SD.Bitmap frame, ulong[] fh)
    {
        var floating = _floating!;
        if (IsUnchangedAt(floating, fh, floating._lastTop))
            return new PlaceResult(false, false, true); // paused inside the floating segment

        int fhLen = fh.Length;
        int? pFloat = floating.FindPlacement(fh, floating._lastTop - fhLen, floating._lastTop + fhLen)
                   ?? floating.FindPlacement(fh, int.MinValue, int.MaxValue);
        int? pMain = FindPlacement(fh, int.MinValue, int.MaxValue);

        if (pMain is int pm && pFloat is int pf)
        {
            // This frame sits at row pm in main and pf in floating: main-row(floatRow) = floatRow + (pm - pf).
            MergeFloating(floating, pm - pf);
            floating.Dispose();
            _floating = null;
            Apply(frame, fh, pm); // make sure the bridge frame itself is committed
            return new PlaceResult(true, false, HasGap);
        }
        if (pFloat is int pf2)
        {
            floating.Apply(frame, fh, pf2); // keep growing the floating segment
            return new PlaceResult(true, false, true);
        }
        if (pMain is int pm2)
        {
            Apply(frame, fh, pm2); // back in known territory; floating stays pending
            return new PlaceResult(true, false, true);
        }

        // Matches neither: a second discontinuity. Abandon the old floating detour for this one.
        floating.Dispose();
        _floating = new ScrollCanvas();
        _floating.Seed(frame, fh);
        return new PlaceResult(true, true, true);
    }

    /// <summary>The stitched image to show/keep: the main canvas, plus any still-pending floating
    /// segment appended below a marker band (its true position is unknown, so it can only be shown
    /// as "skipped, then this"). Caller owns the returned bitmap.</summary>
    public SD.Bitmap? Flatten()
    {
        if (_img is null)
            return null;
        if (_floating?._img is null)
            return (SD.Bitmap)_img.Clone();

        var seg = _floating._img;
        int w = Math.Max(_img.Width, seg.Width);
        int h = _img.Height + MarkerGapPx + seg.Height;
        if (h > MaxHeight) h = MaxHeight;
        var outImg = new SD.Bitmap(w, h, PixelFormat.Format32bppArgb);
        CopyBlock(_img, 0, outImg, 0, Math.Min(_img.Height, h));
        FillBand(outImg, _img.Height, Math.Min(MarkerGapPx, h - _img.Height), MarkerColor);
        int segY = _img.Height + MarkerGapPx;
        if (segY < h)
            CopyBlock(seg, 0, outImg, segY, Math.Min(seg.Height, h - segY));
        return outImg;
    }

    private static bool IsUnchangedAt(ScrollCanvas c, ulong[] fh, int top)
    {
        int n = c._hash.Count;
        for (int y = 0; y < fh.Length; y++)
        {
            int cy = top + y;
            if (cy < 0 || cy >= n || !c._filled[cy] || c._hash[cy] != fh[y]) return false;
        }
        return true;
    }

    /// <summary>
    /// Finds the canvas row <c>p</c> (frame top) maximizing the longest unbroken run of rows matching
    /// FILLED canvas rows and containing a distinctive (non-repeated) frame row, over p in [lo,hi]
    /// (clamped to feasible overlap). Null if no run reaches <see cref="MinRun"/>. The distinctive
    /// requirement stops a blank band from anchoring a frame.
    /// </summary>
    private int? FindPlacement(ulong[] fh, int lo, int hi)
    {
        int n = _hash.Count, fhLen = fh.Length;
        lo = Math.Max(lo, -(fhLen - MinRun));
        hi = Math.Min(hi, n - MinRun);
        if (lo > hi) return null;

        var counts = new Dictionary<ulong, int>(fhLen);
        foreach (ulong h in fh)
            counts[h] = counts.TryGetValue(h, out int c) ? c + 1 : 1;

        int bestP = 0, bestRun = 0;
        for (int p = lo; p <= hi; p++)
        {
            int run = 0, distinct = 0, bestHere = 0;
            for (int y = 0; y < fhLen; y++)
            {
                int cy = p + y;
                if (cy < 0) { run = 0; distinct = 0; continue; }
                if (cy >= n) break;
                if (_filled[cy] && _hash[cy] == fh[y])
                {
                    run++;
                    if (counts[fh[y]] == 1) distinct++;
                    if (distinct > 0 && run > bestHere) bestHere = run;
                }
                else { run = 0; distinct = 0; }
            }
            if (bestHere > bestRun) { bestRun = bestHere; bestP = p; }
        }
        return bestRun >= MinRun ? bestP : null;
    }

    /// <summary>Writes the frame at canvas-row <paramref name="p"/>, growing the canvas up/down first
    /// so [p, p+frameH) fits. Any gap rows it covers become filled.</summary>
    private void Apply(SD.Bitmap frame, ulong[] fh, int p)
    {
        int fhLen = fh.Length;
        int up = Math.Max(0, -p);
        int down = Math.Max(0, (p + fhLen) - _hash.Count);
        if (up > 0)
        {
            if (_img!.Height + up > MaxHeight) return; // would exceed the cap; drop rather than corrupt
            GrowTop(up);
            p += up;
        }
        if (down > 0)
        {
            if (_img!.Height + down > MaxHeight) return;
            GrowBottom(down);
        }
        BlitFrame(frame, fh, p);
        _lastTop = p;
    }

    /// <summary>Blits every FILLED row of <paramref name="seg"/> into this canvas at
    /// main-row = segRow + <paramref name="offset"/>, growing to fit — the gap-closing merge.</summary>
    private void MergeFloating(ScrollCanvas seg, int offset)
    {
        if (seg._img is null) return;
        int segH = seg._img.Height;
        int up = Math.Max(0, -offset);
        int down = Math.Max(0, (offset + segH) - _hash.Count);
        if (_img!.Height + up + down > MaxHeight) return; // too tall to merge safely; leave for Flatten
        if (up > 0) { GrowTop(up); offset += up; }
        if (down > 0) GrowBottom(down);

        for (int r = 0; r < segH; r++)
        {
            if (!seg._filled[r]) continue;
            int cy = offset + r;
            CopyBlock(seg._img, r, _img!, cy, 1);
            _hash[cy] = seg._hash[r];
            _filled[cy] = true;
        }
    }

    private void Seed(SD.Bitmap frame, ulong[] fh)
    {
        _width = frame.Width;
        _img = new SD.Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        CopyBlock(frame, 0, _img, 0, frame.Height);
        for (int y = 0; y < fh.Length; y++) { _hash.Add(fh[y]); _filled.Add(true); }
        _lastTop = 0;
    }

    private void GrowTop(int k)
    {
        var grown = new SD.Bitmap(_width, _img!.Height + k, PixelFormat.Format32bppArgb);
        FillBand(grown, 0, k, MarkerColor);
        CopyBlock(_img, 0, grown, k, _img.Height);
        _img.Dispose();
        _img = grown;
        for (int i = 0; i < k; i++) { _hash.Insert(0, 0); _filled.Insert(0, false); }
        _lastTop += k;
    }

    private void GrowBottom(int k)
    {
        int oldH = _img!.Height;
        var grown = new SD.Bitmap(_width, oldH + k, PixelFormat.Format32bppArgb);
        CopyBlock(_img, 0, grown, 0, oldH);
        FillBand(grown, oldH, k, MarkerColor);
        _img.Dispose();
        _img = grown;
        for (int i = 0; i < k; i++) { _hash.Add(0); _filled.Add(false); }
    }

    private void BlitFrame(SD.Bitmap frame, ulong[] fh, int p)
    {
        CopyBlock(frame, 0, _img!, p, frame.Height);
        for (int y = 0; y < fh.Length; y++)
        {
            int cy = p + y;
            _hash[cy] = fh[y];
            _filled[cy] = true;
        }
    }

    private static void CopyBlock(SD.Bitmap src, int srcY, SD.Bitmap dst, int dstY, int rows)
    {
        if (rows <= 0) return;
        int w = Math.Min(src.Width, dst.Width);
        var s = src.LockBits(new SD.Rectangle(0, srcY, w, rows), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var d = dst.LockBits(new SD.Rectangle(0, dstY, w, rows), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < rows; y++)
                {
                    Marshal.Copy(s.Scan0 + y * s.Stride, row, 0, row.Length);
                    Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, row.Length);
                }
            }
            finally { dst.UnlockBits(d); }
        }
        finally { src.UnlockBits(s); }
    }

    private static void FillBand(SD.Bitmap bmp, int y0, int rows, SD.Color color)
    {
        if (rows <= 0) return;
        var data = bmp.LockBits(new SD.Rectangle(0, y0, bmp.Width, rows), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bmp.Width * 4];
            for (int x = 0; x < bmp.Width; x++)
            {
                int i = x * 4;
                row[i] = color.B; row[i + 1] = color.G; row[i + 2] = color.R; row[i + 3] = 255;
            }
            for (int y = 0; y < rows; y++)
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
        }
        finally { bmp.UnlockBits(data); }
    }

    public void Dispose()
    {
        _img?.Dispose();
        _floating?.Dispose();
    }
}
