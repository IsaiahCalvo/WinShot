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

    private static SD.Bitmap? RenderSvgIcon(string assetName, int pixelSize, SD.Color color)
    {
        try
        {
            using Stream? stream = typeof(SvgIcons).Assembly.GetManifestResourceStream(
                $"WinShot.Assets.{assetName}");
            if (stream is null)
                return null;

            XDocument document = XDocument.Load(stream);
            XElement? root = document.Root;
            if (root is null)
                return null;

            W.Rect viewBox = ParseViewBox(root.Attribute("viewBox")?.Value);
            double inset = Math.Max(1, pixelSize * 0.06);
            double scale = Math.Min((pixelSize - inset * 2) / viewBox.Width, (pixelSize - inset * 2) / viewBox.Height);
            var transform = new WM.MatrixTransform(new WM.Matrix(scale, 0, 0, scale,
                (pixelSize - viewBox.Width * scale) / 2 - viewBox.X * scale,
                (pixelSize - viewBox.Height * scale) / 2 - viewBox.Y * scale));
            var iconBrush = new WM.SolidColorBrush(WM.Color.FromArgb(color.A, color.R, color.G, color.B));

            var visual = new WM.DrawingVisual();
            using (WM.DrawingContext context = visual.RenderOpen())
            {
                context.PushTransform(transform);
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
                    context.DrawGeometry(fill, pen, geometry);
                }
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
