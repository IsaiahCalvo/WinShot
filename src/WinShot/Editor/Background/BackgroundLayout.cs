using SD = System.Drawing;

namespace WinShot.Editor.Background;

public readonly record struct BackgroundLayoutResult(SD.Size SourceSize, SD.Size CanvasSize, int Margin);

/// <summary>The four gutters around the shot, in source pixels.</summary>
public readonly record struct BackgroundMargins(int Left, int Top, int Right, int Bottom);

public static class BackgroundLayout
{
    /// <summary>Inset frame width, in source pixels, at each end of the Inset slider.</summary>
    public const int MaxInset = 64;

    public static BackgroundLayoutResult Calculate(
        SD.Size sourceSize,
        int padding,
        double? aspectRatio,
        int inset = 0)
    {
        int frame = Math.Clamp(inset, 0, MaxInset);
        // The inset is a frame drawn AROUND the shot, so it grows the thing being
        // padded rather than the padding. Padding stays the guaranteed backdrop gutter.
        int sourceWidth = Math.Max(1, sourceSize.Width) + 2 * frame;
        int sourceHeight = Math.Max(1, sourceSize.Height) + 2 * frame;
        int margin = Math.Max(0, padding);
        int contentWidth = sourceWidth + 2 * margin;
        int contentHeight = sourceHeight + 2 * margin;
        int canvasWidth = contentWidth;
        int canvasHeight = contentHeight;

        if (aspectRatio is double ratio && ratio > 0 && !double.IsNaN(ratio) && !double.IsInfinity(ratio))
        {
            if (contentWidth / (double)contentHeight >= ratio)
                canvasHeight = (int)Math.Ceiling(contentWidth / ratio);
            else
                canvasWidth = (int)Math.Ceiling(contentHeight * ratio);
        }

        return new BackgroundLayoutResult(
            new SD.Size(sourceWidth, sourceHeight),
            new SD.Size(canvasWidth, canvasHeight),
            margin);
    }

    /// <summary>
    /// The four gutters that put the shot's VISIBLE content optically centred rather
    /// than its bitmap. A window capture carries its drop shadow baked in and off to one
    /// side, so centring the bitmap leaves the window itself visibly off-centre; adding
    /// the difference back on the tight side fixes it without ever going below
    /// <paramref name="padding"/> on any edge.
    /// </summary>
    /// <param name="source">The shot's full pixel size.</param>
    /// <param name="content">Where the real content sits inside it (see <see cref="ContentBounds"/>).</param>
    public static BackgroundMargins Balance(SD.Size source, SD.Rectangle content, int padding)
    {
        int pad = Math.Max(0, padding);
        var uniform = new BackgroundMargins(pad, pad, pad, pad);
        if (source.Width <= 0 || source.Height <= 0) return uniform;
        if (content.Width <= 0 || content.Height <= 0) return uniform;

        // How much dead space each edge already carries inside the bitmap.
        int left = Math.Max(0, content.Left);
        int top = Math.Max(0, content.Top);
        int right = Math.Max(0, source.Width - content.Right);
        int bottom = Math.Max(0, source.Height - content.Bottom);

        return new BackgroundMargins(
            pad + Math.Max(0, right - left),
            pad + Math.Max(0, bottom - top),
            pad + Math.Max(0, left - right),
            pad + Math.Max(0, top - bottom));
    }

    /// <summary>
    /// Where the real content sits inside a shot: the bitmap trimmed of any transparent
    /// or flat-coloured border. Alpha wins when the bitmap has any — that is a window
    /// capture whose shadow region is genuinely empty; otherwise edges are trimmed while
    /// they stay within <paramref name="tolerance"/> of the corner colour, which is what
    /// a baked shadow or a matte border looks like.
    ///
    /// Returns the whole bitmap when nothing can be trimmed, so the caller's balance is a
    /// no-op rather than a guess.
    /// </summary>
    public static SD.Rectangle ContentBounds(SD.Bitmap source, int tolerance = 16)
    {
        int w = source.Width, h = source.Height;
        var whole = new SD.Rectangle(0, 0, w, h);
        if (w < 3 || h < 3) return whole;

        SD.Imaging.BitmapData data = source.LockBits(
            whole, SD.Imaging.ImageLockMode.ReadOnly, SD.Imaging.PixelFormat.Format32bppArgb);
        byte[] buffer;
        int stride = data.Stride;
        try
        {
            buffer = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
        }
        finally
        {
            source.UnlockBits(data);
        }

        bool hasAlpha = false;
        for (int y = 0; y < h && !hasAlpha; y++)
            for (int x = 0; x < w; x++)
                if (buffer[y * stride + x * 4 + 3] < 250) { hasAlpha = true; break; }

        // The reference the border is measured against: the top-left pixel.
        int cb = buffer[0], cg = buffer[1], cr = buffer[2];

        bool IsBorder(int x, int y)
        {
            int p = y * stride + x * 4;
            if (hasAlpha) return buffer[p + 3] < 250;
            return Math.Abs(buffer[p] - cb) <= tolerance
                && Math.Abs(buffer[p + 1] - cg) <= tolerance
                && Math.Abs(buffer[p + 2] - cr) <= tolerance;
        }

        bool RowIsBorder(int y)
        {
            for (int x = 0; x < w; x++)
                if (!IsBorder(x, y)) return false;
            return true;
        }

        bool ColumnIsBorder(int x, int top, int bottom)
        {
            for (int y = top; y < bottom; y++)
                if (!IsBorder(x, y)) return false;
            return true;
        }

        int t = 0;
        while (t < h - 1 && RowIsBorder(t)) t++;
        int b = h;
        while (b > t + 1 && RowIsBorder(b - 1)) b--;
        int l = 0;
        while (l < w - 1 && ColumnIsBorder(l, t, b)) l++;
        int r = w;
        while (r > l + 1 && ColumnIsBorder(r - 1, t, b)) r--;

        return new SD.Rectangle(l, t, r - l, b - t);
    }
}
