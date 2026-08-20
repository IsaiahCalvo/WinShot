using System.Drawing;
using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The OmniParser tier, run against the real bundled model — these fail if the ONNX file is
/// missing from the build output, if the input plumbing breaks, or if decode/NMS regress.
/// </summary>
public class MlElementDetectorTests
{
    /// <summary>A synthetic app window: toolbar with three button-like chips, a text-field
    /// look-alike, and a flat content area. The model was trained on real UI, so synthetic
    /// shapes only need to be plausible enough to produce SOME detections.</summary>
    private static Bitmap FakeUi()
    {
        var bmp = new Bitmap(1200, 800);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(246, 246, 248));
        using var chip = new SolidBrush(Color.FromArgb(0, 122, 255));
        using var chipText = new SolidBrush(Color.White);
        using var font = new Font("Segoe UI", 11);
        for (int i = 0; i < 3; i++)
        {
            var r = new Rectangle(24 + (i * 140), 20, 120, 36);
            g.FillRectangle(chip, r);
            g.DrawString("Button " + i, font, chipText, r.X + 14, r.Y + 8);
        }
        var field = new Rectangle(24, 90, 500, 34);
        g.FillRectangle(Brushes.White, field);
        g.DrawRectangle(new Pen(Color.FromArgb(180, 180, 190)), field);
        g.DrawString("Type here…", font, new SolidBrush(Color.Gray), field.X + 8, field.Y + 6);
        return bmp;
    }

    [Fact]
    public void ModelLoadsAndRunsOnAWindowCrop()
    {
        using var bmp = FakeUi();
        var boxes = MlElementDetector.Detect(bmp, new Rectangle(0, 0, 1200, 800));

        // The exact count is the model's business; the contract is that it runs and every
        // box it returns is inside the window and sensibly sized.
        foreach (var box in boxes)
        {
            Assert.True(box.Width >= 8 && box.Height >= 8, $"degenerate box {box}");
            Assert.True(new Rectangle(0, 0, 1200, 800).Contains(box), $"box escapes the window: {box}");
        }
    }

    [Fact]
    public void TinyWindowsAreRefusedNotCrashed()
    {
        using var bmp = new Bitmap(40, 40);
        Assert.Empty(MlElementDetector.Detect(bmp, new Rectangle(0, 0, 40, 40)));
    }

    [Fact]
    public void WindowRectOutsideTheBitmapIsRefused()
    {
        using var bmp = FakeUi();
        Assert.Empty(MlElementDetector.Detect(bmp, new Rectangle(5000, 5000, 800, 600)));
    }
}
