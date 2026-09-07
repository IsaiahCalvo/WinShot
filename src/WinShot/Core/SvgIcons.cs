using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using SD = System.Drawing;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace WinShot.Core;

/// <summary>
/// Rasterized SVG button glyphs, keyed by asset + size + colour. Building one means loading an
/// embedded resource, parsing XML, building WPF geometry and rendering it — cached for the
/// process: a handful of small bitmaps shared by every surface that draws the icon set.
/// Callers must not dispose the returned bitmaps.
/// </summary>
public static class SvgIcons
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int, int), SD.Bitmap?>
        IconCache = new();

    public static SD.Bitmap? Get(string assetName, int pixelSize, SD.Color color)
        => IconCache.GetOrAdd((assetName, pixelSize, color.ToArgb()),
            key => RenderSvgIcon(key.Item1, key.Item2, SD.Color.FromArgb(key.Item3)));

    /// <summary>
    /// The box a glyph's ink occupies once it has been fitted into a
    /// <paramref name="pixelSize"/> button — the renderer's own content bounds and scale.
    /// Exposed because this is the only way to assert optical size: WPF's
    /// <c>RenderTargetBitmap</c> draws nothing in a host that has never shown a window, so a
    /// pixel scan in the test suite measures a blank bitmap and proves nothing.
    /// </summary>
    public static W.Size? OpticalExtent(string assetName, int pixelSize)
    {
        XElement? root = LoadRoot(assetName);
        if (root is null)
            return null;

        List<(WM.Geometry Geometry, WM.Brush? Fill, WM.Pen? Pen)> shapes = BuildShapes(root, WM.Brushes.White);
        (W.Rect content, double scale) = Fit(root, shapes, pixelSize);
        return new W.Size(content.Width * scale, content.Height * scale);
    }

    private static XElement? LoadRoot(string assetName)
    {
        using Stream? stream = typeof(SvgIcons).Assembly.GetManifestResourceStream(
            $"WinShot.Assets.{assetName}");
        return stream is null ? null : XDocument.Load(stream).Root;
    }

    private static List<(WM.Geometry Geometry, WM.Brush? Fill, WM.Pen? Pen)> BuildShapes(
        XElement root, WM.Brush iconBrush)
    {
        var shapes = new List<(WM.Geometry Geometry, WM.Brush? Fill, WM.Pen? Pen)>();
        foreach (XElement element in root.Descendants())
        {
            WM.Geometry? geometry = element.Name.LocalName switch
            {
                "path" when !string.IsNullOrWhiteSpace(element.Attribute("d")?.Value)
                    => WM.Geometry.Parse(element.Attribute("d")!.Value),
                "rect" => CreateRectGeometry(element),
                _ => null,
            };
            if (geometry is null)
                continue;

            string fillValue = AttributeValue(element, root, "fill", "none");
            string strokeValue = AttributeValue(element, root, "stroke", "none");
            WM.Brush? fill = fillValue.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : iconBrush;
            WM.Pen? pen = null;
            if (!strokeValue.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                double width = ParseDouble(AttributeValue(element, root, "stroke-width", "1"), 1);
                pen = new WM.Pen(iconBrush, width)
                {
                    StartLineCap = WM.PenLineCap.Round,
                    EndLineCap = WM.PenLineCap.Round,
                    LineJoin = WM.PenLineJoin.Round,
                };
            }
            shapes.Add((geometry, fill, pen));
        }
        return shapes;
    }

    /// <summary>
    /// The share of the button a glyph's ink fills. This is the size the card's own X has
    /// always drawn — 10.2 px inside a 20 px button — and every glyph is now normalized to
    /// it. Content fitting on its own normalizes UPWARD, to whatever the button will hold:
    /// that equalized the family by inflating the X 73 % and the lock 144 %, which restyled
    /// the pinned quick-actions card rather than tidying it. Normalizing DOWN, to the size
    /// that shipped, is the same equalization without the restyle.
    /// </summary>
    private const double ContentBoxFraction = 0.51;

    /// <summary>
    /// Fit by CONTENT, not by the declared viewBox. Two icons drawn on the same 24-grid
    /// rarely fill the same fraction of it — scaling by the box made the pin render ~75%
    /// larger than the X beside it, which is the "vibe-coded" tell the audit named. The union
    /// of the rendered (stroke-inclusive) bounds is the real optical extent, so fitting that
    /// to <see cref="ContentBoxFraction"/> of the button gives every glyph the same visual
    /// weight at the weight the card already had.
    /// </summary>
    private static (W.Rect Content, double Scale) Fit(
        XElement root,
        List<(WM.Geometry Geometry, WM.Brush? Fill, WM.Pen? Pen)> shapes,
        int pixelSize)
    {
        W.Rect content = ContentBounds(shapes);
        if (content.IsEmpty || content.Width <= 0 || content.Height <= 0)
            content = ParseViewBox(root.Attribute("viewBox")?.Value);

        double box = pixelSize * ContentBoxFraction;
        return (content, Math.Min(box / content.Width, box / content.Height));
    }

    private static SD.Bitmap? RenderSvgIcon(string assetName, int pixelSize, SD.Color color)
    {
        try
        {
            XElement? root = LoadRoot(assetName);
            if (root is null)
                return null;

            var iconBrush = new WM.SolidColorBrush(WM.Color.FromArgb(color.A, color.R, color.G, color.B));
            List<(WM.Geometry Geometry, WM.Brush? Fill, WM.Pen? Pen)> shapes = BuildShapes(root, iconBrush);

            (W.Rect content, double scale) = Fit(root, shapes, pixelSize);
            var transform = new WM.MatrixTransform(new WM.Matrix(scale, 0, 0, scale,
                (pixelSize - content.Width * scale) / 2 - content.X * scale,
                (pixelSize - content.Height * scale) / 2 - content.Y * scale));

            var visual = new WM.DrawingVisual();
            using (WM.DrawingContext context = visual.RenderOpen())
            {
                context.PushTransform(transform);
                foreach ((WM.Geometry geometry, WM.Brush? fill, WM.Pen? pen) in shapes)
                    context.DrawGeometry(fill, pen, geometry);
                context.Pop();
            }

            var rendered = new WMI.RenderTargetBitmap(
                pixelSize,
                pixelSize,
                96,
                96,
                WM.PixelFormats.Pbgra32);
            rendered.Render(visual);

            var bitmap = new SD.Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppPArgb);
            BitmapData data = bitmap.LockBits(
                new SD.Rectangle(0, 0, pixelSize, pixelSize),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                rendered.CopyPixels(
                    new W.Int32Rect(0, 0, pixelSize, pixelSize),
                    data.Scan0,
                    data.Stride * data.Height,
                    data.Stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error($"SVG icon '{assetName}' could not be rendered", ex);
            return null;
        }
    }

    /// <summary>Union of every shape's rendered extent, stroke included — the box the glyph
    /// actually occupies inside its grid.</summary>
    private static W.Rect ContentBounds(List<(WM.Geometry Geometry, WM.Brush? Fill, WM.Pen? Pen)> shapes)
    {
        W.Rect bounds = W.Rect.Empty;
        foreach ((WM.Geometry geometry, _, WM.Pen? pen) in shapes)
        {
            W.Rect shape = pen is null ? geometry.Bounds : geometry.GetRenderBounds(pen);
            if (!shape.IsEmpty)
                bounds.Union(shape);
        }
        return bounds;
    }

    private static W.Rect ParseViewBox(string? value)
    {
        string[] parts = (value ?? "0 0 24 24").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4
            ? new W.Rect(ParseDouble(parts[0], 0), ParseDouble(parts[1], 0), ParseDouble(parts[2], 24), ParseDouble(parts[3], 24))
            : new W.Rect(0, 0, 24, 24);
    }

    private static WM.Geometry CreateRectGeometry(XElement element)
    {
        double x = ParseDouble(element.Attribute("x")?.Value, 0);
        double y = ParseDouble(element.Attribute("y")?.Value, 0);
        double width = ParseDouble(element.Attribute("width")?.Value, 0);
        double height = ParseDouble(element.Attribute("height")?.Value, 0);
        double radius = ParseDouble(element.Attribute("rx")?.Value, 0);
        return new WM.RectangleGeometry(new W.Rect(x, y, width, height), radius, radius);
    }

    private static string AttributeValue(XElement element, XElement root, string name, string fallback)
        => element.Attribute(name)?.Value ?? root.Attribute(name)?.Value ?? fallback;

    private static double ParseDouble(string? value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
}
