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
/// Four independent colour-delta rays cast from one pixel of a frozen bitmap: left and right
/// walk that pixel's scanline, top and bottom walk its column, and each stops at the first
/// pixel that is no longer close to the <em>fixed</em> starting pixel. That makes it a
/// contiguous-run walk rather than a gradient detector — and it is why hovering an empty gap
/// measures the gap for free, the same as hovering an object measures the object.
///
/// The bitmap is locked once for the life of the instance (a selector session) and read
/// stride-indexed, because a row's pitch is not width * 4 and <c>Bitmap.GetPixel</c> would
/// re-enter GDI+ per pixel.
/// </summary>
public sealed class EdgeSnap : IDisposable
{
    /// <summary>PowerToys' tuned default for the summed-L1 metric.</summary>
    public const byte DefaultTolerance = 30;

    private readonly SD.Bitmap _bitmap;
    private SDI.BitmapData? _data;
    private IntPtr _scan0;
    private int _stride;

    private EdgeSnap(SD.Bitmap bitmap)
    {
        _bitmap = bitmap;
        Width = bitmap.Width;
        Height = bitmap.Height;
        _data = bitmap.LockBits(
            new SD.Rectangle(0, 0, Width, Height),
            SDI.ImageLockMode.ReadOnly,
            SDI.PixelFormat.Format32bppArgb);
        _scan0 = _data.Scan0;
        _stride = _data.Stride;
    }

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
    public SD.Rectangle DetectEdges(SD.Point cursor, byte tolerance, bool perChannel = false)
    {
        // The walk clamps the cursor to [1, dim-2] so it can never read out of bounds; a
        // bitmap thinner than three pixels has no room for that and is simply all one run.
        if (_data is null || Width < 3 || Height < 3)
            return new SD.Rectangle(0, 0, Width, Height);

        int left = FindEdge(cursor, tolerance, perChannel, isX: true, increment: false);
        int top = FindEdge(cursor, tolerance, perChannel, isX: false, increment: false);
        int right = FindEdge(cursor, tolerance, perChannel, isX: true, increment: true);
        int bottom = FindEdge(cursor, tolerance, perChannel, isX: false, increment: true);
        return SD.Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private int FindEdge(SD.Point centre, byte tolerance, bool perChannel, bool isX, bool increment)
    {
        int maxDim = isX ? Width : Height;
        int x = Math.Clamp(centre.X, 1, Width - 2);
        int y = Math.Clamp(centre.Y, 1, Height - 2);

        uint startPixel = PixelAt(x, y);
        while (true)
        {
            int oldX = x;
            int oldY = y;
            if (isX)
            {
                if (increment) { if (++x == maxDim) break; }
                else { if (--x == 0) break; }
            }
            else
            {
                if (increment) { if (++y == maxDim) break; }
                else { if (--y == 0) break; }
            }

            // Always against the START pixel, never the neighbour: a run, not a gradient.
            if (!PixelsClose(startPixel, PixelAt(x, y), tolerance, perChannel))
                return isX ? oldX : oldY;
        }

        // The ray reached the frame edge — degrade to it rather than fail.
        return increment ? maxDim - 1 : 0;
    }

    /// <summary>
    /// PowerToys' two metrics over the four bytes of a pixel. Per-channel is strict: every
    /// channel must be within tolerance. The default is summed L1, masked to a byte exactly as
    /// the SIMD original masks <c>_mm_sad_epu8</c>'s result with <c>uint8_t</c>'s max — a sum
    /// past 255 wraps there, and the tuned default of 30 was tuned against that behaviour.
    /// </summary>
    internal static bool PixelsClose(uint pixel1, uint pixel2, byte tolerance, bool perChannel)
    {
        int d0 = Math.Abs((int)(pixel1 & 0xFF) - (int)(pixel2 & 0xFF));
        int d1 = Math.Abs((int)((pixel1 >> 8) & 0xFF) - (int)((pixel2 >> 8) & 0xFF));
        int d2 = Math.Abs((int)((pixel1 >> 16) & 0xFF) - (int)((pixel2 >> 16) & 0xFF));
        int d3 = Math.Abs((int)((pixel1 >> 24) & 0xFF) - (int)((pixel2 >> 24) & 0xFF));

        if (perChannel)
            return d0 <= tolerance && d1 <= tolerance && d2 <= tolerance && d3 <= tolerance;

        return ((d0 + d1 + d2 + d3) & 0xFF) <= tolerance;
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
