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
