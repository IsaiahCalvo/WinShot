using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WinShot.Core;
using SD = System.Drawing;
using Shapes = System.Windows.Shapes;

namespace WinShot.Editor;

/// <summary>
/// Serializable description of a single annotation (project schema v1). An instance
/// is attached to each committed canvas element's <c>Tag</c> so "Save project" can
/// round-trip data that is not recoverable from the WPF visual alone (arrow endpoints,
/// step numbers, text styles, …). All geometry is in content pixels (1 DIP = 1 source
/// pixel); <see cref="Tx"/>/<see cref="Ty"/> carry the accumulated translate offset
/// from moves and crop shifts and are refreshed from the element at save time, as is
/// <see cref="Z"/> (the canvas child index).
/// </summary>
internal sealed class AnnotationData
{
    public const string TypeArrow = "arrow";
    public const string TypeCurvedArrow = "curvedArrow";
    public const string TypeLine = "line";
    public const string TypeRectangle = "rectangle";
    public const string TypeEllipse = "ellipse";
    public const string TypeFreehand = "freehand";
    public const string TypeHighlighter = "highlighter";
    public const string TypeText = "text";
    public const string TypeEmoji = "emoji";
    public const string TypeStep = "step";
    public const string TypeSpotlight = "spotlight";
    public const string TypeImage = "image";

    public string Type { get; set; } = "";

    /// <summary>Geometry points: arrow/line = [from, to]; curved arrow = [from, control, to];
    /// freehand/highlighter = the polyline points; text/emoji/step = [topLeft].</summary>
    public double[][]? Points { get; set; }

    /// <summary>[x, y, w, h] for rectangles, ellipses, the spotlight hole and image annotations.</summary>
    public double[]? Rect { get; set; }

    /// <summary>[w, h] of the image at spotlight creation time (the dim layer's outer bounds).</summary>
    public double[]? Outer { get; set; }

    /// <summary>"#AARRGGBB". For highlighter strokes this includes the baked-in alpha.</summary>
    public string? Color { get; set; }

    public double? Thickness { get; set; }

    /// <summary><see cref="ShapeFillMode"/> name for rectangles/ellipses (None | Quarter | Solid).</summary>
    public string? Fill { get; set; }

    public string? Text { get; set; }

    /// <summary><see cref="TextStyle"/> name for text annotations (Plain | Bold | Outline | Pill | Huge).</summary>
    public string? Style { get; set; }

    public double? FontSize { get; set; }

    /// <summary>Step badge number.</summary>
    public int? Number { get; set; }

    /// <summary>Index into the project's embedded images/{n}.png entries.</summary>
    [JsonPropertyName("image")]
    public int? ImageIndex { get; set; }

    /// <summary>Z-order (AnnotationCanvas child index at save time).</summary>
    public int Z { get; set; }

    /// <summary>Accumulated TranslateTransform offset (Select-tool moves + crop shifts).</summary>
    public double Tx { get; set; }

    public double Ty { get; set; }

    public AnnotationData Clone() => (AnnotationData)MemberwiseClone();

    // ------------------------------ factory helpers used at commit time

    public static AnnotationData ForStroke(string type, IEnumerable<Point> points, Color color, double thickness) => new()
    {
        Type = type,
        Points = ToArray(points),
        Color = ToHex(color),
        Thickness = thickness,
    };

    public static AnnotationData ForShape(string type, Rect bounds, Color color, double thickness, ShapeFillMode fill) => new()
    {
        Type = type,
        Rect = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height },
        Color = ToHex(color),
        Thickness = thickness,
        Fill = fill.ToString(),
    };

    public static AnnotationData ForText(Point topLeft, string text, TextStyle style, double fontSize, Color color) => new()
    {
        Type = TypeText,
        Points = ToArray(new[] { topLeft }),
        Text = text,
        Style = style.ToString(),
        FontSize = fontSize,
        Color = ToHex(color),
    };

    public static AnnotationData ForEmoji(Point topLeft, string emoji) => new()
    {
        Type = TypeEmoji,
        Points = ToArray(new[] { topLeft }),
        Text = emoji,
    };

    public static AnnotationData ForStep(Point topLeft, int number, Color color, double thickness) => new()
    {
        Type = TypeStep,
        Points = ToArray(new[] { topLeft }),
        Number = number,
        Color = ToHex(color),
        Thickness = thickness,
    };

    public static AnnotationData ForSpotlight(Size outer, Rect hole) => new()
    {
        Type = TypeSpotlight,
        Outer = new[] { outer.Width, outer.Height },
        Rect = new[] { hole.X, hole.Y, hole.Width, hole.Height },
    };

    public static AnnotationData ForImage(Rect bounds) => new()
    {
        Type = TypeImage,
        Rect = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height },
    };

    private static double[][] ToArray(IEnumerable<Point> points) =>
        points.Select(p => new[] { p.X, p.Y }).ToArray();

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>Root object of annotations.json (schema v1).</summary>
internal sealed class ProjectDocument
{
    public int Version { get; set; } = 1;
    public List<AnnotationData> Annotations { get; set; } = new();
}

/// <summary>
/// Reads and writes the non-destructive .winshot project format: a ZIP archive
/// containing source.png (the current source bitmap, including any baked
/// blur/pixelate/crop), annotations.json (schema v1) and images/{n}.png entries
/// for embedded image annotations. Also rebuilds the live WPF elements from the
/// parsed data and decodes external image files for the multi-image feature.
/// </summary>
internal static class ProjectSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Writes a project file, replacing any existing file at <paramref name="path"/>.</summary>
    public static void Save(string path, SD.Bitmap source, ProjectDocument doc, IReadOnlyList<BitmapSource> images)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        // PNG payloads are already deflate-compressed; storing them avoids double work.
        var sourceEntry = zip.CreateEntry("source.png", CompressionLevel.NoCompression);
        using (var ms = new MemoryStream())
        {
            source.Save(ms, SD.Imaging.ImageFormat.Png);
            ms.Position = 0;
            using var es = sourceEntry.Open();
            ms.CopyTo(es);
        }

        var jsonEntry = zip.CreateEntry("annotations.json", CompressionLevel.Optimal);
        using (var es = jsonEntry.Open())
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(doc, JsonOptions);
            es.Write(json, 0, json.Length);
        }

        for (int i = 0; i < images.Count; i++)
        {
            var entry = zip.CreateEntry($"images/{i}.png", CompressionLevel.NoCompression);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(images[i]));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            using var es = entry.Open();
            ms.CopyTo(es);
        }
    }

    /// <summary>
    /// Reads a project file. Throws (InvalidDataException, JsonException, …) on any
    /// malformed content; the caller decides how to surface the failure. The returned
    /// bitmap is owned by the caller.
    /// </summary>
    public static (SD.Bitmap Source, ProjectDocument Doc, List<BitmapSource> Images) Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var sourceEntry = zip.GetEntry("source.png")
            ?? throw new InvalidDataException("Project is missing source.png.");
        SD.Bitmap source;
        using (var ms = CopyToMemory(sourceEntry))
        using (var decoded = new SD.Bitmap(ms))
            source = new SD.Bitmap(decoded); // copy once more to detach from the stream

        try
        {
            var jsonEntry = zip.GetEntry("annotations.json")
                ?? throw new InvalidDataException("Project is missing annotations.json.");
            ProjectDocument doc;
            using (var ms = CopyToMemory(jsonEntry))
                doc = JsonSerializer.Deserialize<ProjectDocument>(ms.ToArray(), JsonOptions)
                    ?? throw new InvalidDataException("annotations.json is empty.");
            if (doc.Version != 1)
                throw new InvalidDataException($"Unsupported project version {doc.Version}.");

            int maxIndex = doc.Annotations
                .Where(a => a.ImageIndex is not null)
                .Select(a => a.ImageIndex!.Value)
                .DefaultIfEmpty(-1)
                .Max();
            var images = new List<BitmapSource>();
            for (int i = 0; i <= maxIndex; i++)
            {
                var entry = zip.GetEntry($"images/{i}.png")
                    ?? throw new InvalidDataException($"Project is missing images/{i}.png.");
                using var ms = CopyToMemory(entry);
                var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (frame.CanFreeze) frame.Freeze();
                images.Add(frame);
            }
            return (source, doc, images);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the live canvas element for one annotation, positioned in content
    /// coordinates (the caller applies the translate offset and adds it to the canvas).
    /// Throws InvalidDataException on malformed or unknown entries.
    /// </summary>
    public static UIElement CreateElement(AnnotationData a, IReadOnlyList<BitmapSource> images)
    {
        switch (a.Type)
        {
            case AnnotationData.TypeArrow:
            case AnnotationData.TypeCurvedArrow:
            {
                bool curved = a.Type == AnnotationData.TypeCurvedArrow;
                var pts = RequirePoints(a, curved ? 3 : 2);
                double t = RequireThickness(a);
                var brush = new SolidColorBrush(RequireColor(a));
                return new Shapes.Path
                {
                    Stroke = brush,
                    Fill = brush,
                    StrokeThickness = t,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = curved
                        ? AnnotationFactory.CurvedArrowGeometry(pts[0], pts[1], pts[2], t)
                        : AnnotationFactory.ArrowGeometry(pts[0], pts[1], t),
                };
            }
            case AnnotationData.TypeLine:
            {
                var pts = RequirePoints(a, 2);
                return new Shapes.Line
                {
                    X1 = pts[0].X, Y1 = pts[0].Y, X2 = pts[1].X, Y2 = pts[1].Y,
                    Stroke = new SolidColorBrush(RequireColor(a)),
                    StrokeThickness = RequireThickness(a),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
            }
            case AnnotationData.TypeRectangle:
            case AnnotationData.TypeEllipse:
            {
                var bounds = RequireRect(a);
                var color = RequireColor(a);
                Shapes.Shape shape = a.Type == AnnotationData.TypeRectangle
                    ? new Shapes.Rectangle { RadiusX = 2, RadiusY = 2 }
                    : new Shapes.Ellipse();
                shape.Stroke = new SolidColorBrush(color);
                shape.StrokeThickness = RequireThickness(a);
                shape.Fill = FillBrush(a.Fill, color);
                shape.Width = bounds.Width;
                shape.Height = bounds.Height;
                SetPos(shape, bounds.TopLeft);
                return shape;
            }
            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
            {
                var pts = RequirePoints(a, 2);
                return new Shapes.Polyline
                {
                    Stroke = new SolidColorBrush(RequireColor(a)),
                    StrokeThickness = RequireThickness(a),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Points = new PointCollection(pts),
                };
            }
            case AnnotationData.TypeText:
            {
                var pts = RequirePoints(a, 1);
                string text = a.Text
                    ?? throw new InvalidDataException("Text annotation is missing its text.");
                if (a.FontSize is not double fontSize || fontSize <= 0)
                    throw new InvalidDataException("Text annotation is missing its font size.");
                Enum.TryParse(a.Style, out TextStyle style); // unknown style → Plain
                var label = AnnotationFactory.CreateStyledTextLabel(
                    text, new SolidColorBrush(RequireColor(a)), fontSize, style);
                SetPos(label, pts[0]);
                return label;
            }
            case AnnotationData.TypeEmoji:
            {
                var pts = RequirePoints(a, 1);
                string emoji = a.Text
                    ?? throw new InvalidDataException("Emoji annotation is missing its text.");
                var label = AnnotationFactory.CreateEmojiLabel(emoji);
                SetPos(label, pts[0]);
                return label;
            }
            case AnnotationData.TypeStep:
            {
                var pts = RequirePoints(a, 1);
                int number = a.Number
                    ?? throw new InvalidDataException("Step annotation is missing its number.");
                var badge = AnnotationFactory.CreateStepBadge(number, RequireColor(a), RequireThickness(a));
                SetPos(badge, pts[0]);
                return badge;
            }
            case AnnotationData.TypeSpotlight:
            {
                var hole = RequireRect(a);
                if (a.Outer is not { Length: >= 2 } outer || outer[0] <= 0 || outer[1] <= 0)
                    throw new InvalidDataException("Spotlight annotation is missing its outer size.");
                return AnnotationFactory.CreateSpotlight(new Size(outer[0], outer[1]), hole);
            }
            case AnnotationData.TypeImage:
            {
                var bounds = RequireRect(a);
                int index = a.ImageIndex
                    ?? throw new InvalidDataException("Image annotation is missing its image index.");
                if (index < 0 || index >= images.Count)
                    throw new InvalidDataException($"Image annotation index {index} is out of range.");
                var img = new Image
                {
                    Source = images[index],
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Stretch = Stretch.Fill,
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                SetPos(img, bounds.TopLeft);
                return img;
            }
            default:
                throw new InvalidDataException($"Unknown annotation type '{a.Type}'.");
        }
    }

    /// <summary>
    /// Decodes an image file into a frozen BitmapSource for use as an image annotation.
    /// Tries WPF/WIC first, then SkiaSharp (which covers WebP without the OS codec pack).
    /// Returns null (and logs) when the file is not a decodable image.
    /// </summary>
    public static BitmapSource? LoadImageFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (frame.CanFreeze) frame.Freeze();
            return frame;
        }
        catch (Exception wicEx)
        {
            try
            {
                using var sk = SKBitmap.Decode(path);
                using var image = sk is null ? null : SKImage.FromBitmap(sk);
                using var data = image?.Encode(SKEncodedImageFormat.Png, 100);
                if (data is null)
                {
                    Log.Error($"Not a decodable image file: {path}", wicEx);
                    return null;
                }
                using var ms = new MemoryStream();
                data.SaveTo(ms);
                ms.Position = 0;
                var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (frame.CanFreeze) frame.Freeze();
                return frame;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to decode image file: {path}", ex);
                return null;
            }
        }
    }

    // ------------------------------------------------------------- helpers

    private static MemoryStream CopyToMemory(ZipArchiveEntry entry)
    {
        var ms = new MemoryStream();
        using (var es = entry.Open())
            es.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    private static void SetPos(UIElement element, Point p)
    {
        Canvas.SetLeft(element, p.X);
        Canvas.SetTop(element, p.Y);
    }

    private static Point[] RequirePoints(AnnotationData a, int count)
    {
        if (a.Points is null || a.Points.Length < count || a.Points.Any(p => p is null || p.Length < 2))
            throw new InvalidDataException($"Annotation '{a.Type}' needs at least {count} point(s).");
        return a.Points.Select(p => new Point(p[0], p[1])).ToArray();
    }

    private static Rect RequireRect(AnnotationData a)
    {
        if (a.Rect is not { Length: >= 4 } r || r[2] < 0 || r[3] < 0)
            throw new InvalidDataException($"Annotation '{a.Type}' has an invalid rect.");
        return new Rect(r[0], r[1], r[2], r[3]);
    }

    private static Color RequireColor(AnnotationData a)
    {
        string hex = a.Color
            ?? throw new InvalidDataException($"Annotation '{a.Type}' is missing its color.");
        return ColorConverter.ConvertFromString(hex) is Color c
            ? c
            : throw new InvalidDataException($"Annotation '{a.Type}' has an invalid color '{hex}'.");
    }

    private static double RequireThickness(AnnotationData a) =>
        a.Thickness is double t && t > 0
            ? t
            : throw new InvalidDataException($"Annotation '{a.Type}' is missing its thickness.");

    /// <summary>Mirrors the editor's ShapeFill logic for reconstructed rectangles/ellipses.</summary>
    private static Brush? FillBrush(string? mode, Color color) =>
        Enum.TryParse(mode, out ShapeFillMode fill)
            ? fill switch
            {
                ShapeFillMode.Quarter => new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)),
                ShapeFillMode.Solid => new SolidColorBrush(color),
                _ => null,
            }
            : null;
}
