using System.IO;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Reading an image off the clipboard. WinShot has always written to the clipboard and never
/// read from it, so opening a picture someone pasted you meant saving it to a file first.
///
/// Driven through a stub payload rather than the real clipboard: the hidden test desktop has
/// no clipboard of its own, and a suite that fights the machine's clipboard is a flaky suite.
/// </summary>
public class ClipboardImageTests
{
    private sealed class Payload : WF.IDataObject
    {
        private readonly Dictionary<string, object> _items = new(StringComparer.OrdinalIgnoreCase);

        public Payload With(string format, object value)
        {
            _items[format] = value;
            return this;
        }

        public object? GetData(string format) => _items.GetValueOrDefault(format);
        public object? GetData(string format, bool autoConvert) => GetData(format);
        public object? GetData(Type format) => GetData(format.FullName!);
        public bool GetDataPresent(string format) => _items.ContainsKey(format);
        public bool GetDataPresent(string format, bool autoConvert) => GetDataPresent(format);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!);
        public string[] GetFormats() => _items.Keys.ToArray();
        public string[] GetFormats(bool autoConvert) => GetFormats();
        public void SetData(object? data) => throw new NotSupportedException();
        public void SetData(string format, object? data) => throw new NotSupportedException();
        public void SetData(string format, bool autoConvert, object? data) => throw new NotSupportedException();
        public void SetData(Type format, object? data) => throw new NotSupportedException();
    }

    private static MemoryStream Png(Action<SD.Bitmap> paint)
    {
        using var bitmap = new SD.Bitmap(4, 4, SD.Imaging.PixelFormat.Format32bppArgb);
        paint(bitmap);
        var stream = new MemoryStream();
        bitmap.Save(stream, SD.Imaging.ImageFormat.Png);
        return stream;
    }

    [Fact]
    public void NothingOnTheClipboardIsNotAnErrorItIsJustNothingToOpen()
    {
        Assert.Null(ClipboardImage.FromDataObject(null));
        Assert.Null(ClipboardImage.FromDataObject(new Payload()));
        Assert.Null(ClipboardImage.FromDataObject(new Payload().With(WF.DataFormats.Text, "not a picture")));
    }

    [Fact]
    public void APngPayloadComesBackWithItsAlphaIntact()
    {
        using MemoryStream png = Png(b =>
        {
            b.SetPixel(0, 0, SD.Color.FromArgb(0, 0, 0, 0));
            b.SetPixel(1, 1, SD.Color.FromArgb(255, 10, 20, 30));
        });

        using SD.Bitmap? read = ClipboardImage.FromDataObject(new Payload().With("PNG", png));

        Assert.NotNull(read);
        // The DIB path Clipboard.GetImage() uses loses alpha, which turns a rounded-corner
        // screenshot's transparent corners black. PNG is read first for exactly this.
        Assert.Equal(0, read!.GetPixel(0, 0).A);
        Assert.Equal(SD.Color.FromArgb(255, 10, 20, 30), read.GetPixel(1, 1));
    }

    [Fact]
    public void PngWinsOverTheBitmapFormatWhenBothArePresent()
    {
        using MemoryStream png = Png(b => b.SetPixel(0, 0, SD.Color.FromArgb(0, 0, 0, 0)));
        using var dib = new SD.Bitmap(4, 4, SD.Imaging.PixelFormat.Format24bppRgb);

        using SD.Bitmap? read = ClipboardImage.FromDataObject(
            new Payload().With(WF.DataFormats.Bitmap, dib).With("PNG", png));

        Assert.NotNull(read);
        Assert.Equal(0, read!.GetPixel(0, 0).A);
    }

    [Fact]
    public void TheBitmapFormatIsStillReadWhenThereIsNoPng()
    {
        using var dib = new SD.Bitmap(6, 3, SD.Imaging.PixelFormat.Format24bppRgb);
        using (var g = SD.Graphics.FromImage(dib))
            g.Clear(SD.Color.Red);

        using SD.Bitmap? read = ClipboardImage.FromDataObject(new Payload().With(WF.DataFormats.Bitmap, dib));

        Assert.NotNull(read);
        Assert.Equal(6, read!.Width);
        Assert.Equal(SD.Color.Red.ToArgb(), read.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void AnImageFileCopiedInExplorerCountsAsAnImage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winshot-clip-{Guid.NewGuid():N}.png");
        try
        {
            using (var bitmap = new SD.Bitmap(5, 5))
            using (var g = SD.Graphics.FromImage(bitmap))
            {
                g.Clear(SD.Color.Lime);
                bitmap.Save(path, SD.Imaging.ImageFormat.Png);
            }

            using SD.Bitmap? read = ClipboardImage.FromDataObject(
                new Payload().With(WF.DataFormats.FileDrop, new[] { path }));

            Assert.NotNull(read);
            Assert.Equal(5, read!.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileThatIsNotAnImageIsSkippedRatherThanThrown()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winshot-clip-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "definitely not a picture");

            Assert.Null(ClipboardImage.FromDataObject(
                new Payload().With(WF.DataFormats.FileDrop, new[] { "C:/does/not/exist.png", path })));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CorruptPngDataFallsThroughToTheNextFormat()
    {
        using var garbage = new MemoryStream([1, 2, 3, 4, 5]);
        using var dib = new SD.Bitmap(7, 2, SD.Imaging.PixelFormat.Format24bppRgb);

        using SD.Bitmap? read = ClipboardImage.FromDataObject(
            new Payload().With("PNG", garbage).With(WF.DataFormats.Bitmap, dib));

        Assert.NotNull(read);
        Assert.Equal(7, read!.Width);
    }
}
