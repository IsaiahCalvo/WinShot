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
/// Renders an embedded SVG asset (WinShot.Assets.*) into a monochrome-tinted GDI
/// bitmap for the Fast* surfaces. Understands the Lucide subset: path, rect,
/// circle, ellipse, line, polyline, polygon — stroke and fill, element-first with
/// root fallback. Any authored color is replaced by the tint; pens are forced
/// round cap/join (matches Lucide's 1.85 stroke language).
/// Grown out of FastQuickActionsWindow.CreateSvgIcon.
/// </summary>
public static class SvgIcon
{
    public static SD.Bitmap? Render(string assetName, int pixelSize, SD.Color color)
    {
        try
        {
            using Stream? stream = typeof(SvgIcon).Assembly.GetManifestResourceStream(
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
                    WM.Geometry? geometry = CreateGeometry(element);
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

            var rendered = new WMI.RenderTargetBitmap(pixelSize, pixelSize, 96, 96, WM.PixelFormats.Pbgra32);
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

    private static WM.Geometry? CreateGeometry(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "path" when !string.IsNullOrWhiteSpace(element.Attribute("d")?.Value):
                return WM.Geometry.Parse(element.Attribute("d")!.Value);
            case "rect":
            {
                double x = Attr(element, "x"), y = Attr(element, "y");
                double w = Attr(element, "width"), h = Attr(element, "height");
                double r = Attr(element, "rx");
                return new WM.RectangleGeometry(new W.Rect(x, y, w, h), r, r);
            }
            case "circle":
            {
                double r = Attr(element, "r");
                return new WM.EllipseGeometry(new W.Point(Attr(element, "cx"), Attr(element, "cy")), r, r);
            }
            case "ellipse":
                return new WM.EllipseGeometry(
                    new W.Point(Attr(element, "cx"), Attr(element, "cy")),
                    Attr(element, "rx"), Attr(element, "ry"));
            case "line":
                return new WM.LineGeometry(
                    new W.Point(Attr(element, "x1"), Attr(element, "y1")),
                    new W.Point(Attr(element, "x2"), Attr(element, "y2")));
            case "polyline":
            case "polygon":
            {
                var points = ParsePoints(element.Attribute("points")?.Value);
                if (points.Count < 2) return null;
                var figure = new WM.PathFigure { StartPoint = points[0], IsClosed = element.Name.LocalName == "polygon" };
                for (int i = 1; i < points.Count; i++)
                    figure.Segments.Add(new WM.LineSegment(points[i], isStroked: true));
                var geometry = new WM.PathGeometry();
                geometry.Figures.Add(figure);
                return geometry;
            }
            default:
                return null;
        }
    }

    private static List<W.Point> ParsePoints(string? value)
    {
        var result = new List<W.Point>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        string[] parts = value.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < parts.Length; i += 2)
            result.Add(new W.Point(ParseDouble(parts[i], 0), ParseDouble(parts[i + 1], 0)));
        return result;
    }

    private static double Attr(XElement element, string name) => ParseDouble(element.Attribute(name)?.Value, 0);

    private static W.Rect ParseViewBox(string? value)
    {
        string[] parts = (value ?? "0 0 24 24").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4
            ? new W.Rect(ParseDouble(parts[0], 0), ParseDouble(parts[1], 0), ParseDouble(parts[2], 24), ParseDouble(parts[3], 24))
            : new W.Rect(0, 0, 24, 24);
    }

    private static string AttributeValue(XElement element, XElement root, string name, string fallback)
        => element.Attribute(name)?.Value ?? root.Attribute(name)?.Value ?? fallback;

    private static double ParseDouble(string? value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
}
