using WinShot.Ocr;
using Xunit;

namespace WinShot.Tests;

public class OcrClipboardFormatterTests
{
    [Fact]
    public void Build_ReturnsNullWhenNothingWasFound()
    {
        var payload = OcrClipboardFormatter.Build(new OcrCaptureResult("", []));

        Assert.Null(payload);
    }

    [Fact]
    public void Build_UsesQrOnlyTitleWhenOnlyCodeWasFound()
    {
        var payload = OcrClipboardFormatter.Build(new OcrCaptureResult("", ["https://example.com"]));

        Assert.NotNull(payload);
        Assert.Equal("https://example.com", payload.ClipboardText);
        Assert.Equal("QR code copied", payload.BalloonTitle);
        Assert.Equal("https://example.com", payload.Preview);
    }

    [Fact]
    public void Build_SeparatesTextAndCodesWhenBothWereFound()
    {
        var payload = OcrClipboardFormatter.Build(new OcrCaptureResult("Room 204", ["WIFI:S:Guest;"]));

        Assert.NotNull(payload);
        Assert.Equal($"Room 204{Environment.NewLine}{Environment.NewLine}WIFI:S:Guest;", payload.ClipboardText);
        Assert.Equal("Text + QR code copied", payload.BalloonTitle);
    }
}
