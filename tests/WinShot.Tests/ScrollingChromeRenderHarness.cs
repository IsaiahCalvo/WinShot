using System.Drawing.Imaging;
using System.IO;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Opt-in, sanitized rendering harness for the scrolling-capture controls. It draws the
/// WinForms surface in-process, so evidence does not contain any desktop or user content.
/// </summary>
public class ScrollingChromeRenderHarness
{
    [Fact]
    public void RenderScrollingControlsToPng()
    {
        string? outDir = Environment.GetEnvironmentVariable("WINSHOT_SCROLL_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
            return;

        Directory.CreateDirectory(outDir);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var controls = new ScrollControlsBar(new SD.Rectangle(100, 100, 900, 600));
                controls.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                controls.Location = new SD.Point(-10_000, -10_000);
                controls.Show();
                controls.Location = new SD.Point(-10_000, -10_000);
                System.Windows.Forms.Application.DoEvents();
                controls.CreateControl();
                controls.PerformLayout();
                Save(controls, Path.Combine(outDir, "controls-ready.png"));

                controls.StartAutomatically(auto: false);
                controls.SetStatus("8 frames - 2,840px");
                controls.PerformLayout();
                Save(controls, Path.Combine(outDir, "controls-capturing.png"));

                controls.SetHint(ScrollHint.SlowDown);
                controls.PerformLayout();
                Save(controls, Path.Combine(outDir, "controls-recovery.png"));
                controls.Hide();
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
        Assert.Equal(3, Directory.GetFiles(outDir, "*.png").Length);
    }

    private static void Save(System.Windows.Forms.Form form, string path)
    {
        var size = form.PreferredSize;
        form.ClientSize = size;
        using var bitmap = new SD.Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        form.DrawToBitmap(bitmap, new SD.Rectangle(SD.Point.Empty, bitmap.Size));
        bitmap.Save(path, ImageFormat.Png);
    }
}
