using System.IO.Compression;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class ProjectSerializerTests
{
    [Fact]
    public void CreateElement_RestoresDoubleArrowAndLetterStepStyles()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var arrow = Assert.IsType<System.Windows.Shapes.Path>(ProjectSerializer.CreateElement(
                    new AnnotationData
                    {
                        Type = AnnotationData.TypeArrow,
                        Points = new[] { new[] { 0d, 0d }, new[] { 100d, 0d } },
                        Color = "#FFFF453A",
                        Thickness = 4,
                        Style = ArrowStyle.Double.ToString(),
                    }, Array.Empty<BitmapSource>()));
                var geometry = Assert.IsType<PathGeometry>(arrow.Data);
                Assert.Equal(3, geometry.Figures.Count); // shaft plus a head at both ends

                var step = Assert.IsType<System.Windows.Controls.Grid>(ProjectSerializer.CreateElement(
                    new AnnotationData
                    {
                        Type = AnnotationData.TypeStep,
                        Points = new[] { new[] { 10d, 20d } },
                        Number = 1,
                        Color = "#FF34C759",
                        Thickness = 4,
                        Style = "Letter",
                    }, Array.Empty<BitmapSource>()));
                var caption = Assert.IsType<System.Windows.Controls.TextBlock>(step.Children[1]);
                Assert.Equal("A", caption.Text);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void SaveAndLoad_PreservesArrowAndLetterStepStyleMetadata()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"winshot-style-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "styles.winshot");
        try
        {
            using var source = new SD.Bitmap(4, 3);
            var doc = new ProjectDocument
            {
                Annotations =
                {
                    new AnnotationData
                    {
                        Type = AnnotationData.TypeArrow,
                        Points = new[] { new[] { 0d, 0d }, new[] { 3d, 2d } },
                        Color = "#FFFF453A",
                        Thickness = 4,
                        Style = ArrowStyle.Thin.ToString(),
                    },
                    new AnnotationData
                    {
                        Type = AnnotationData.TypeStep,
                        Points = new[] { new[] { 1d, 1d } },
                        Number = 2,
                        Color = "#FF34C759",
                        Thickness = 4,
                        Style = "Letter",
                    },
                },
            };

            ProjectSerializer.Save(path, source, doc, Array.Empty<BitmapSource>());
            var loaded = ProjectSerializer.Load(path);
            using var loadedSource = loaded.Source;
            Assert.Equal("Thin", loaded.Doc.Annotations[0].Style);
            Assert.Equal("Letter", loaded.Doc.Annotations[1].Style);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSurveyDrawShapeTextMetadata()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"winshot-survey-style-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "survey.winshot");
        try
        {
            using var source = new SD.Bitmap(8, 6);
            var doc = new ProjectDocument
            {
                Annotations =
                {
                    new AnnotationData
                    {
                        Type = AnnotationData.TypeArrow,
                        Points = new[] { new[] { 0d, 0d }, new[] { 6d, 2d } },
                        Color = "#FFFF0000",
                        Thickness = 3,
                        Head = "vShape",
                        LineStyle = "dashed",
                    },
                    new AnnotationData
                    {
                        Type = AnnotationData.TypeRectangle,
                        Rect = new[] { 1d, 1d, 4d, 3d },
                        Color = "#FF00FF00",
                        FillColor = "#400000FF",
                        Thickness = 2,
                        Fill = "Solid",
                        LineStyle = "dotted",
                    },
                    AnnotationData.ForCallout(
                        CalloutLayout.FromDrag(new Point(0, 0), new Point(3, 2)),
                        "hello", Color.FromRgb(255, 0, 0), Color.FromArgb(0, 255, 255, 255),
                        2, ArrowheadStyle.OpenCircle, LineBorderStyle.Solid, 16),
                },
            };

            ProjectSerializer.Save(path, source, doc, Array.Empty<BitmapSource>());
            var loaded = ProjectSerializer.Load(path);
            using var loadedSource = loaded.Source;
            Assert.Equal("vShape", loaded.Doc.Annotations[0].Head);
            Assert.Equal("dashed", loaded.Doc.Annotations[0].LineStyle);
            Assert.Equal("#400000FF", loaded.Doc.Annotations[1].FillColor);
            Assert.Equal("dotted", loaded.Doc.Annotations[1].LineStyle);
            Assert.Equal(AnnotationData.TypeCallout, loaded.Doc.Annotations[2].Type);
            Assert.Equal("hello", loaded.Doc.Annotations[2].Text);
            Assert.Equal("openCircle", loaded.Doc.Annotations[2].Head);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateElement_RebuildsSurveyArrowheadAndCallout()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var arrow = Assert.IsType<System.Windows.Shapes.Path>(ProjectSerializer.CreateElement(
                    new AnnotationData
                    {
                        Type = AnnotationData.TypeArrow,
                        Points = new[] { new[] { 0d, 0d }, new[] { 80d, 0d } },
                        Color = "#FFFF453A",
                        Thickness = 4,
                        Head = "openTriangle",
                        LineStyle = "dashed",
                    }, Array.Empty<BitmapSource>()));
                var geometry = Assert.IsType<PathGeometry>(arrow.Data);
                Assert.Equal(2, geometry.Figures.Count);
                Assert.False(geometry.Figures[1].IsFilled);
                Assert.NotNull(arrow.StrokeDashArray);
                Assert.Equal(new[] { 6d, 4d }, arrow.StrokeDashArray.ToArray());

                var callout = Assert.IsType<CalloutAnnotation>(ProjectSerializer.CreateElement(
                    AnnotationData.ForCallout(
                        CalloutLayout.FromDrag(new Point(4, 4), new Point(40, 20)),
                        "note", Color.FromRgb(30, 41, 59), Color.FromArgb(0, 255, 255, 255),
                        2, ArrowheadStyle.SolidTriangle, LineBorderStyle.Dotted, 14),
                    Array.Empty<BitmapSource>()));
                Assert.Equal("note", callout.Text);
                Assert.True(callout.Layout.Box.Width >= CalloutLayout.MinBoxWidth);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsMultipleEmbeddedImages()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"winshot-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "multi.winshot");

        try
        {
            using var source = new SD.Bitmap(4, 3);
            var first = AnnotationData.ForImage(new Rect(1, 2, 3, 4));
            first.ImageIndex = 0;
            var second = AnnotationData.ForImage(new Rect(6, 7, 8, 9));
            second.ImageIndex = 1;
            var doc = new ProjectDocument();
            doc.Annotations.Add(first);
            doc.Annotations.Add(second);

            ProjectSerializer.Save(path, source, doc, new[]
            {
                CreateBitmapSource(2, 3, Colors.Red),
                CreateBitmapSource(5, 7, Colors.Blue),
            });

            using (var zip = ZipFile.OpenRead(path))
            {
                Assert.NotNull(zip.GetEntry("source.png"));
                Assert.NotNull(zip.GetEntry("annotations.json"));
                Assert.NotNull(zip.GetEntry("images/0.png"));
                Assert.NotNull(zip.GetEntry("images/1.png"));
            }

            var loaded = ProjectSerializer.Load(path);
            using var loadedSource = loaded.Source;

            Assert.Equal(4, loadedSource.Width);
            Assert.Equal(3, loadedSource.Height);
            Assert.Equal(2, loaded.Doc.Annotations.Count);
            Assert.Equal(new int?[] { 0, 1 }, loaded.Doc.Annotations.Select(a => a.ImageIndex).ToArray());
            Assert.Equal(2, loaded.Images.Count);
            Assert.Equal(2, loaded.Images[0].PixelWidth);
            Assert.Equal(3, loaded.Images[0].PixelHeight);
            Assert.Equal(5, loaded.Images[1].PixelWidth);
            Assert.Equal(7, loaded.Images[1].PixelHeight);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static BitmapSource CreateBitmapSource(int width, int height, Color color)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        source.Freeze();
        return source;
    }
}
