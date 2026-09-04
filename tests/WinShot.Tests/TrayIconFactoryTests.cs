using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class TrayIconFactoryTests
{
    /// <summary>The tray swaps between these on every take, and Icon.FromHandle does not own
    /// its HICON — a fresh icon per swap would leak a GDI handle per recording.</summary>
    [Fact]
    public void EachIconIsBuiltOnceAndReused()
    {
        Assert.Same(TrayIconFactory.Create(), TrayIconFactory.Create());
        Assert.Same(TrayIconFactory.CreateRecording(), TrayIconFactory.CreateRecording());
        Assert.NotSame(TrayIconFactory.Create(), TrayIconFactory.CreateRecording());
    }

    [Fact]
    public void TheRecordingIconIsVisiblyDifferentFromTheIdleOne()
    {
        using SD.Bitmap idle = TrayIconFactory.Create().ToBitmap();
        using SD.Bitmap recording = TrayIconFactory.CreateRecording().ToBitmap();

        Assert.Equal(idle.Size, recording.Size);
        int differing = 0;
        for (int y = 0; y < idle.Height; y++)
        {
            for (int x = 0; x < idle.Width; x++)
            {
                if (idle.GetPixel(x, y) != recording.GetPixel(x, y))
                    differing++;
            }
        }

        // A tinted-but-identical silhouette would be unreadable at 16 px; the stop square has
        // to change a real share of the icon.
        Assert.True(differing > idle.Width * idle.Height / 10, $"only {differing} pixels differ");
    }

    [Fact]
    public void TheRecordingIconIsRedAtItsCentreEdge()
    {
        using SD.Bitmap recording = TrayIconFactory.CreateRecording().ToBitmap();

        // Just inside the disc on the left, clear of the white stop square.
        SD.Color body = recording.GetPixel(5, 16);
        Assert.True(body.R > body.G + 60 && body.R > body.B + 60, body.ToString());
    }
}
