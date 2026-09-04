using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SDPixel = System.Drawing.Imaging.PixelFormat;

namespace WinShot.Editor;

/// <summary>
/// Survey highlighter uses SVG <c>mix-blend-mode: multiply</c> against the page.
/// WPF cannot multiply a Path against the screenshot behind it, so live ink stays
/// an alpha fill and flatten (copy / save / pin / drag) composites each highlighter
/// with the CSS multiply-then-src-over formula, in z-order.
/// </summary>
internal static class HighlighterBlend
{
    /// <summary>
    /// CSS mix-blend-mode:multiply against an opaque backdrop, then src-over with
    /// source alpha: <c>Co = Cb * (1 - αs + αs * Cs)</c>.
    /// </summary>
    public static void MultiplyOnto(SD.Bitmap dest, SD.Bitmap overlay)
    {
        Composite(dest, overlay, multiply: true);
    }

    public static void SrcOver(SD.Bitmap dest, SD.Bitmap overlay)
    {
        Composite(dest, overlay, multiply: false);
    }

    /// <summary>
    /// Flatten CanvasHost: screenshot first, then each annotation in z-order.
    /// Highlighters multiply; everything else src-overs. When there are no
    /// highlighters this is the same as a single <see cref="BitmapEffects.RenderVisual"/>.
    /// </summary>
    public static SD.Bitmap Flatten(
        FrameworkElement canvasHost, UIElement screenshot, Canvas annotations, int pixelWidth, int pixelHeight)
    {
        var children = annotations.Children.Cast<UIElement>().ToList();
        bool anyHighlighter = children.Any(IsHighlighter);
        if (!anyHighlighter)
            return BitmapEffects.RenderVisual(canvasHost, pixelWidth, pixelHeight);

        var previous = children.Select(el => el.Visibility).ToArray();
        var shotWas = screenshot.Visibility;
        try
        {
            foreach (var el in children)
                el.Visibility = Visibility.Hidden;
            screenshot.Visibility = Visibility.Visible;
            canvasHost.UpdateLayout();
            var dest = BitmapEffects.RenderVisual(canvasHost, pixelWidth, pixelHeight);

            screenshot.Visibility = Visibility.Hidden;
            for (int i = 0; i < children.Count; i++)
            {
                if (previous[i] != Visibility.Visible) continue;
                var el = children[i];
                el.Visibility = Visibility.Visible;
                canvasHost.UpdateLayout();
                using var overlay = BitmapEffects.RenderVisual(canvasHost, pixelWidth, pixelHeight);
                if (IsHighlighter(el)) MultiplyOnto(dest, overlay);
                else SrcOver(dest, overlay);
                el.Visibility = Visibility.Hidden;
            }

            return dest;
        }
        finally
        {
            screenshot.Visibility = shotWas;
            for (int i = 0; i < children.Count; i++)
                children[i].Visibility = previous[i];
        }
    }

    /// <summary>
    /// Everything painted BELOW <paramref name="target"/>: the screenshot plus every
    /// annotation under it, exactly as the live canvas shows them. Survey's
    /// mix-blend-mode:multiply blends a highlighter against the page AND every earlier
    /// sibling in the same SVG (svgAnnotationRenderers.jsx:272; the SVG root sets no
    /// isolation), so this - not the bare screenshot - is a highlighter's backdrop.
    /// </summary>
    public static SD.Bitmap BackdropUnder(
        FrameworkElement canvasHost, UIElement screenshot, Canvas annotations, UIElement target,
        int pixelWidth, int pixelHeight)
    {
        var children = annotations.Children.Cast<UIElement>().ToList();
        int limit = children.IndexOf(target);
        if (limit < 0) limit = children.Count;
        var previous = children.Select(el => el.Visibility).ToArray();
        var shotWas = screenshot.Visibility;
        try
        {
            screenshot.Visibility = Visibility.Visible;
            for (int i = limit; i < children.Count; i++)
                children[i].Visibility = Visibility.Hidden;
            canvasHost.UpdateLayout();
            return BitmapEffects.RenderVisual(canvasHost, pixelWidth, pixelHeight);
        }
        finally
        {
            screenshot.Visibility = shotWas;
            for (int i = 0; i < children.Count; i++)
                children[i].Visibility = previous[i];
            canvasHost.UpdateLayout();
        }
    }

    /// <summary>
    /// Live-preview paint for the in-flight highlighter polyline: the whole screenshot
    /// multiplied by the highlighter colour, mapped 1:1 onto the canvas. Survey's drag
    /// preview already carries mixBlendMode:'multiply' at the colour and width the commit
    /// will use (SVGAnnotationLayer.jsx:5028-5039), so the stroke must not change
    /// appearance on release. Composited once at pointer-down, then only re-clipped.
    /// </summary>
    public static Brush PreviewBrush(SD.Bitmap screenshot, Color color)
    {
        if (screenshot.Width < 1 || screenshot.Height < 1)
            return new SolidColorBrush(color);
        using var dest = screenshot.Clone(
            new SD.Rectangle(0, 0, screenshot.Width, screenshot.Height), SDPixel.Format32bppArgb);
        using var overlay = new SD.Bitmap(dest.Width, dest.Height, SDPixel.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(overlay))
            g.Clear(SD.Color.FromArgb(color.A, color.R, color.G, color.B));
        MultiplyOnto(dest, overlay);
        return BlendBrush(dest, new Rect(0, 0, dest.Width, dest.Height));
    }

    public static bool IsHighlighter(UIElement el) =>
        el is FrameworkElement fe && fe.Tag is AnnotationData meta &&
        meta.Type == AnnotationData.TypeHighlighter;

    /// <summary>
    /// Live canvas fill: the Path is clipped to the ink, and the fill is the crop of
    /// <paramref name="backdrop"/> under it multiplied by the highlighter color - the same
    /// CSS formula Flatten uses. Pass the composite from <see cref="BackdropUnder"/> so the
    /// blend sees the annotations below the ink, not just the page. Rebuild after a move;
    /// Flatten remains authoritative.
    /// </summary>
    public static void ApplyLiveFill(System.Windows.Shapes.Path ink, SD.Bitmap backdrop, Color color)
    {
        if (ink.Data is null)
        {
            ink.Fill = new SolidColorBrush(color);
            return;
        }

        Rect bounds = ink.Data.Bounds;
        if (bounds.Width < 1 || bounds.Height < 1 || backdrop.Width < 1)
        {
            ink.Fill = new SolidColorBrush(color);
            return;
        }

        int x = (int)Math.Floor(bounds.X);
        int y = (int)Math.Floor(bounds.Y);
        int w = Math.Max(1, (int)Math.Ceiling(bounds.Width) + 1);
        int h = Math.Max(1, (int)Math.Ceiling(bounds.Height) + 1);
        var crop = new SD.Rectangle(x, y, w, h);
        crop.Intersect(new SD.Rectangle(0, 0, backdrop.Width, backdrop.Height));
        if (crop.Width < 1 || crop.Height < 1)
        {
            ink.Fill = new SolidColorBrush(color);
            return;
        }

        using var dest = backdrop.Clone(crop, SDPixel.Format32bppArgb);
        using var overlay = new SD.Bitmap(crop.Width, crop.Height, SDPixel.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(overlay))
            g.Clear(SD.Color.FromArgb(color.A, color.R, color.G, color.B));
        MultiplyOnto(dest, overlay);
        ink.Fill = BlendBrush(dest, new Rect(crop.X, crop.Y, crop.Width, crop.Height));
    }

    /// <summary>
    /// The blended crop, mapped 1:1 onto <paramref name="viewport"/> in canvas coordinates.
    /// Survey's blend is evaluated by the browser per DEVICE pixel, so the pixels under a
    /// highlight are exactly as crisp as the pixels beside it at any zoom. WPF resamples this
    /// bitmap through the zoom transform instead, so it must at least use the SAME filter as
    /// the page underneath (EditorWindow.SetBaseTiles pins BaseTiles to HighQuality/Fant);
    /// on the default Linear filter the highlighted band was visibly softer than its
    /// surroundings at every zoom other than 100%.
    /// </summary>
    private static ImageBrush BlendBrush(SD.Bitmap blended, Rect viewport)
    {
        var brush = new ImageBrush(ToBitmapSource(blended))
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = viewport,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        return brush;
    }

    private static BitmapSource ToBitmapSource(SD.Bitmap bitmap)
    {
        using var ms = new System.IO.MemoryStream();
        bitmap.Save(ms, SD.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Unit-testable pixel formula for one channel.</summary>
    public static byte MultiplyChannel(byte backdrop, byte source, byte sourceAlpha)
    {
        double a = sourceAlpha / 255.0;
        double cs = source / 255.0;
        double cb = backdrop / 255.0;
        return (byte)Math.Clamp(Math.Round(255 * cb * (1 - a + a * cs)), 0, 255);
    }

    private static void Composite(SD.Bitmap dest, SD.Bitmap overlay, bool multiply)
    {
        if (dest.Width != overlay.Width || dest.Height != overlay.Height)
            throw new ArgumentException("Overlay must match dest size.");

        var destData = dest.LockBits(
            new SD.Rectangle(0, 0, dest.Width, dest.Height),
            SD.Imaging.ImageLockMode.ReadWrite, SDPixel.Format32bppArgb);
        var overData = overlay.LockBits(
            new SD.Rectangle(0, 0, overlay.Width, overlay.Height),
            SD.Imaging.ImageLockMode.ReadOnly, SDPixel.Format32bppArgb);
        try
        {
            int h = dest.Height;
            int destStride = destData.Stride;
            int overStride = overData.Stride;
            var destBuf = new byte[destStride * h];
            var overBuf = new byte[overStride * h];
            System.Runtime.InteropServices.Marshal.Copy(destData.Scan0, destBuf, 0, destBuf.Length);
            System.Runtime.InteropServices.Marshal.Copy(overData.Scan0, overBuf, 0, overBuf.Length);
            int w = dest.Width;
            for (int y = 0; y < h; y++)
            {
                int dRow = y * destStride;
                int oRow = y * overStride;
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4;
                    byte a = overBuf[oRow + i + 3];
                    if (a == 0) continue;
                    if (multiply)
                    {
                        destBuf[dRow + i] = MultiplyChannel(destBuf[dRow + i], overBuf[oRow + i], a);
                        destBuf[dRow + i + 1] = MultiplyChannel(destBuf[dRow + i + 1], overBuf[oRow + i + 1], a);
                        destBuf[dRow + i + 2] = MultiplyChannel(destBuf[dRow + i + 2], overBuf[oRow + i + 2], a);
                    }
                    else
                    {
                        double af = a / 255.0;
                        destBuf[dRow + i] = (byte)Math.Clamp(Math.Round(overBuf[oRow + i] * af + destBuf[dRow + i] * (1 - af)), 0, 255);
                        destBuf[dRow + i + 1] = (byte)Math.Clamp(Math.Round(overBuf[oRow + i + 1] * af + destBuf[dRow + i + 1] * (1 - af)), 0, 255);
                        destBuf[dRow + i + 2] = (byte)Math.Clamp(Math.Round(overBuf[oRow + i + 2] * af + destBuf[dRow + i + 2] * (1 - af)), 0, 255);
                    }
                    destBuf[dRow + i + 3] = 255;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(destBuf, 0, destData.Scan0, destBuf.Length);
        }
        finally
        {
            dest.UnlockBits(destData);
            overlay.UnlockBits(overData);
        }
    }
}
