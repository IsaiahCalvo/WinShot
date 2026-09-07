// -----------------------------------------------------------------------------------------
// Ported from Microsoft PowerToys — src/modules/MeasureTool/MeasureToolCore/EdgeDetection.cpp
// and BGRATextureView.h (https://github.com/microsoft/PowerToys).
//
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// The full MIT permission notice travels with this port in THIRD_PARTY_NOTICES.md at the
// repository root, which ships alongside the binary.
// -----------------------------------------------------------------------------------------

using System.Runtime.InteropServices;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace WinShot.Capture;

/// <summary>
/// A detected run plus how sure each ray is of the edge it stopped on: an edge is firm when
/// the colour change that stopped it cleared the tolerance by a comfortable margin, and soft
/// when it barely did or when the ray simply ran off the frame.
/// </summary>
public readonly record struct EdgeRun(
    SD.Rectangle Bounds, bool LeftFirm, bool TopFirm, bool RightFirm, bool BottomFirm);

/// <summary>
/// Four independent colour-delta rays cast from one pixel of a frozen bitmap: left and right
/// walk that pixel's scanline, top and bottom walk its column, and each stops at the first
/// pixel that is no longer close to the <em>fixed</em> starting pixel. That makes it a
/// contiguous-run walk rather than a gradient detector — and it is why hovering an empty gap
/// measures the gap for free, the same as hovering an object measures the object.
///
/// The bitmap is locked for the life of the instance and read stride-indexed, because a
/// row's pitch is not width * 4 and <c>Bitmap.GetPixel</c> would re-enter GDI+ per pixel.
/// Keep that life short: GDI+ refuses to DRAW a bitmap whose bits are locked, and the
/// selector blits the frozen snapshot on every frame. One lock per read pass, not per session.
/// </summary>
public sealed class EdgeSnap : IDisposable
{
    /// <summary>PowerToys' tuned default for the summed-L1 metric.</summary>
    public const byte DefaultTolerance = 30;

    /// <summary>How far past a stop a ray peeks for the far side of an anti-aliased
    /// transition. Three, because a Windows glyph fringe is one or two blended pixels wide
    /// and anything further away is a different object, not the same one seen through a
    /// fringe.</summary>
    public const int AntiAliasPeek = 3;

    /// <summary>How far past the tolerance a stop has to land before the HUD calls that edge
    /// firm. An edge that only just cleared the knob is exactly the one the user should be
    /// invited to turn the knob for, so it loses its serif instead of claiming a fact.</summary>
    public const int EdgeFirmMargin = 32;

    private readonly SD.Bitmap _bitmap;
    private SDI.BitmapData? _data;
    private IntPtr _scan0;
    private int _stride;

    private EdgeSnap(SD.Bitmap bitmap)
    {
        _bitmap = bitmap;
        Width = bitmap.Width;
        Height = bitmap.Height;
        // Ask for the format the bitmap already is where that is a 32-bit one: a mismatch
        // makes LockBits convert the WHOLE bitmap into a scratch buffer, which on a
        // virtual-desktop snapshot is tens of megabytes per read pass. The three 32-bit
        // layouts differ only in what the top byte means, and the metric below reads all
        // four bytes as a distance, so a constant alpha contributes a constant zero.
        _data = bitmap.LockBits(
            new SD.Rectangle(0, 0, Width, Height),
            SDI.ImageLockMode.ReadOnly,
            LockFormat(bitmap));
        _scan0 = _data.Scan0;
        _stride = _data.Stride;
    }

    private static SDI.PixelFormat LockFormat(SD.Bitmap bitmap) => bitmap.PixelFormat switch
    {
        SDI.PixelFormat.Format32bppArgb or
        SDI.PixelFormat.Format32bppRgb or
        SDI.PixelFormat.Format32bppPArgb => bitmap.PixelFormat,
        _ => SDI.PixelFormat.Format32bppArgb,
    };

    public int Width { get; }

    public int Height { get; }

    /// <summary>Locks <paramref name="frozen"/> for reading until the returned instance is
    /// disposed. The caller must not dispose the bitmap first.</summary>
    public static EdgeSnap Lock(SD.Bitmap frozen)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        return new EdgeSnap(frozen);
    }

    /// <summary>The 32-bit pixel at (x, y); undefined outside the bitmap.</summary>
    public uint PixelAt(int x, int y)
    {
        if (_data is null || x < 0 || y < 0 || x >= Width || y >= Height)
            return 0;
        return unchecked((uint)Marshal.ReadInt32(_scan0, (y * _stride) + (x * 4)));
    }

    public SD.Color ColorAt(int x, int y)
    {
        uint px = PixelAt(x, y);
        return SD.Color.FromArgb((int)((px >> 16) & 0xFF), (int)((px >> 8) & 0xFF), (int)(px & 0xFF));
    }

    /// <summary>
    /// The contiguous run around <paramref name="cursor"/>, as an ordinary rectangle: the four
    /// rays return the LAST similar coordinate on each side, so the run is inclusive on both
    /// ends and <c>Width = right - left + 1</c>. Getting that +1 wrong is the classic off-by-one
    /// in this feature — every measurement would read one pixel short.
    /// </summary>
    public SD.Rectangle DetectEdges(SD.Point cursor, byte tolerance, bool perChannel = false) =>
        DetectRun(cursor, tolerance, perChannel).Bounds;

    /// <summary>
    /// <see cref="DetectEdges"/> plus how sure each of the four rays is of where it stopped,
    /// which is what lets the HUD stop pretending a barely-there edge is a fact.
    /// </summary>
    public EdgeRun DetectRun(SD.Point cursor, byte tolerance, bool perChannel = false)
    {
        // The walk clamps the cursor to [1, dim-2] so it can never read out of bounds; a
        // bitmap thinner than three pixels has no room for that and is simply all one run.
        if (_data is null || Width < 3 || Height < 3)
            return new EdgeRun(new SD.Rectangle(0, 0, Width, Height), false, false, false, false);

        var left = FindEdge(cursor, tolerance, perChannel, isX: true, increment: false);
        var top = FindEdge(cursor, tolerance, perChannel, isX: false, increment: false);
        var right = FindEdge(cursor, tolerance, perChannel, isX: true, increment: true);
        var bottom = FindEdge(cursor, tolerance, perChannel, isX: false, increment: true);
        return new EdgeRun(
            SD.Rectangle.FromLTRB(left.Edge, top.Edge, right.Edge + 1, bottom.Edge + 1),
            left.Firm, top.Firm, right.Firm, bottom.Firm);
    }

    private (int Edge, bool Firm) FindEdge(
        SD.Point centre, byte tolerance, bool perChannel, bool isX, bool increment)
    {
        int maxDim = isX ? Width : Height;
        int x = Math.Clamp(centre.X, 1, Width - 2);
        int y = Math.Clamp(centre.Y, 1, Height - 2);
        int other = isX ? y : x;
        int step = increment ? 1 : -1;
        uint startPixel = PixelAt(x, y);

        uint At(int i) => isX ? PixelAt(i, other) : PixelAt(other, i);

        // Anti-aliasing: a glyph stem, a rounded corner and a hairline stroke all reach the
        // background through one or two BLENDED pixels, and stopping at the first of them
        // measures the stroke narrower than anyone can see it. So peek a little way past the
        // stop for the colour the walk settles into — two consecutive pixels alike — and
        // treat everything between as part of the run when, and only when, every one of
        // those pixels is a channel-wise blend of where the walk started and where it ended.
        //
        // That last clause is the whole safety of this. A 1 px window border or table rule
        // between two patches of the SAME colour settles back onto the start colour, which
        // is not "between" anything: the walk stops at the border, exactly as it should, and
        // a bordered card still measures its border. Only a real transition is crossed.
        int SettledAfter(int first)
        {
            int plateau = first;
            for (int i = 0; i < AntiAliasPeek; i++)
            {
                int next = plateau + step;
                if (next < 0 || next >= maxDim)
                    return first;

                uint here = At(plateau);
                if (PixelsClose(here, At(next), tolerance, perChannel))
                {
                    // Settled. A settled colour that is close to where the walk started is a
                    // thin sliver of something else, not the far side of a transition.
                    if (plateau == first || PixelsClose(startPixel, here, tolerance, perChannel))
                        return first;
                    return IsBlend(first, plateau, here) ? plateau : first;
                }

                plateau = next;
            }
            return first;
        }

        // Every pixel from first up to (not including) the settled one has to lie between the
        // start colour and the settled colour on all four channels.
        bool IsBlend(int first, int settled, uint settledPixel)
        {
            for (int i = first; i != settled; i += step)
            {
                if (!Between(startPixel, At(i), settledPixel))
                    return false;
            }
            return true;
        }

        int at = isX ? x : y;
        while (true)
        {
            int next = at + step;
            if (increment ? next == maxDim : next == 0)
            {
                // The ray reached the frame edge — degrade to it rather than fail. There is
                // no edge there to be sure of, so it is never a firm one.
                return (increment ? maxDim - 1 : 0, false);
            }

            // Always against the START pixel, never the neighbour: a run, not a gradient.
            if (!PixelsClose(startPixel, At(next), tolerance, perChannel))
            {
                int settled = SettledAfter(next);
                int delta = Distance(startPixel, At(settled), perChannel);
                return (settled - step, delta >= tolerance + EdgeFirmMargin);
            }

            at = next;
        }
    }

    /// <summary>Whether every channel of <paramref name="mid"/> sits between the matching
    /// channels of <paramref name="a"/> and <paramref name="b"/>, with a pixel of slack for
    /// the rounding a real compositor does.</summary>
    internal static bool Between(uint a, uint mid, uint b)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            int lo = (int)((a >> shift) & 0xFF);
            int hi = (int)((b >> shift) & 0xFF);
            if (lo > hi)
                (lo, hi) = (hi, lo);
            int m = (int)((mid >> shift) & 0xFF);
            if (m < lo - 1 || m > hi + 1)
                return false;
        }
        return true;
    }

    /// <summary>
    /// PowerToys' two metrics over the four bytes of a pixel. Per-channel is strict: every
    /// channel must be within tolerance. The default is summed L1.
    ///
    /// ONE DELIBERATE DEVIATION FROM THE PORT. The original masks <c>_mm_sad_epu8</c>'s
    /// result with <c>uint8_t</c>'s max, so a summed distance past 255 wraps: a blue button
    /// on a white card scores 224 + 192 + 112 = 528, wraps to 16, and reads as the SAME
    /// COLOUR at any sane tolerance — the ruler walks straight through the edge it exists to
    /// find. This clamps instead of wrapping. Below 256 the two are identical, which is the
    /// entire range the tuned default of 30 lives in, so the tuning is untouched; above it,
    /// pairs that were accidentally "close" are now correctly far apart.
    /// </summary>
    internal static bool PixelsClose(uint pixel1, uint pixel2, byte tolerance, bool perChannel) =>
        Distance(pixel1, pixel2, perChannel) <= tolerance;

    /// <summary>The metric itself, as a number, because the HUD wants to know not only THAT
    /// a ray stopped but how decisively.</summary>
    internal static int Distance(uint pixel1, uint pixel2, bool perChannel)
    {
        int d0 = Math.Abs((int)(pixel1 & 0xFF) - (int)(pixel2 & 0xFF));
        int d1 = Math.Abs((int)((pixel1 >> 8) & 0xFF) - (int)((pixel2 >> 8) & 0xFF));
        int d2 = Math.Abs((int)((pixel1 >> 16) & 0xFF) - (int)((pixel2 >> 16) & 0xFF));
        int d3 = Math.Abs((int)((pixel1 >> 24) & 0xFF) - (int)((pixel2 >> 24) & 0xFF));

        if (perChannel)
            return Math.Max(Math.Max(d0, d1), Math.Max(d2, d3));

        return Math.Min(d0 + d1 + d2 + d3, 255);
    }

    public void Dispose()
    {
        var data = _data;
        _data = null;
        _scan0 = IntPtr.Zero;
        _stride = 0;
        if (data is null)
            return;
        try { _bitmap.UnlockBits(data); }
        catch (Exception) { /* the bitmap was already disposed by someone else */ }
    }
}
