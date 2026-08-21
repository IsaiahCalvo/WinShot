using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class DragPreviewTests
{
    /// <summary>
    /// Smoke test for the drag thumbnail: it must build, size itself, and tear down on an STA
    /// thread without throwing — the window/interop path can't be exercised by a real OLE drag.
    /// </summary>
    [Fact]
    public void Construct_ScalesLongEdgeAndDisposesCleanly()
    {
        Exception? failure = null;
        double width = 0, height = 0;

        var thread = new Thread(() =>
        {
            try
            {
                // No Application instance here on purpose — DragPreview uses only explicit brushes,
                // and constructing one races other STA tests over the WPF Application singleton.
                using var source = new SD.Bitmap(1600, 800);
                var preview = new DragPreview(source);
                preview.MoveToCursor();

                var window = (Window)typeof(DragPreview)
                    .GetField("_window", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(preview)!;
                var image = (Image)((Border)window.Content).Child;
                width = image.Width;
                height = image.Height;

                preview.Dispose();
                preview.Dispose(); // idempotent
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
        Assert.Equal(180, width);   // long edge clamped
        Assert.Equal(90, height);   // aspect preserved
    }
}
