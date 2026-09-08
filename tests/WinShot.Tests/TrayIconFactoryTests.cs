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
    public void TheRecordingBadgeIsRedAndCarriesTheStopSquare()
    {
        using SD.Bitmap recording = TrayIconFactory.CreateRecording().ToBitmap();

        // The badge sits in the bottom-right quadrant, the same corner the confirmation
        // badge uses. Count what it actually paints rather than probing one pixel: the mark
        // underneath is a shipped asset and may move.
        int red = 0, white = 0;
        for (int y = recording.Height / 2; y < recording.Height; y++)
        {
            for (int x = recording.Width / 2; x < recording.Width; x++)
            {
                SD.Color p = recording.GetPixel(x, y);
                if (p.A < 128) continue;
                if (p.R > p.G + 60 && p.R > p.B + 60) red++;
                else if (p.R > 200 && p.G > 200 && p.B > 200) white++;
            }
        }

        Assert.True(red > 60, $"the recording badge is not red enough ({red} px)");
        Assert.True(white > 20, $"the stop square is missing ({white} px)");
    }
}
