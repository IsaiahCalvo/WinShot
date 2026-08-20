using SD = System.Drawing;

namespace WinShot.Recording;

public readonly record struct RecordingRegionSelection(SD.Rectangle ScreenRect)
{
    public bool IsUsable => ScreenRect.Width >= 2 && ScreenRect.Height >= 2;

    public static RecordingRegionSelection FromVirtualSelection(
        SD.Rectangle virtualRegion,
        SD.Rectangle virtualScreen)
    {
        return new RecordingRegionSelection(new SD.Rectangle(
            virtualRegion.X + virtualScreen.X,
            virtualRegion.Y + virtualScreen.Y,
            RoundDownToEven(virtualRegion.Width),
            RoundDownToEven(virtualRegion.Height)));
    }

    public static RecordingRegionSelection FromDisplay(SD.Rectangle displayBounds)
    {
        return new RecordingRegionSelection(new SD.Rectangle(
            displayBounds.X,
            displayBounds.Y,
            RoundDownToEven(displayBounds.Width),
            RoundDownToEven(displayBounds.Height)));
    }

    /// <summary>Union of the chosen displays' bounds (even-rounded for H.264).</summary>
    public static RecordingRegionSelection FromDisplays(IReadOnlyList<SD.Rectangle> displays)
    {
        if (displays.Count == 0)
            return default;
        SD.Rectangle union = displays[0];
        for (int i = 1; i < displays.Count; i++)
            union = SD.Rectangle.Union(union, displays[i]);
        return FromDisplay(union);
    }

    public static RecordingRegionSelection FromScreenSelection(SD.Rectangle screenRegion)
    {
        return new RecordingRegionSelection(new SD.Rectangle(
            screenRegion.X,
            screenRegion.Y,
            RoundDownToEven(screenRegion.Width),
            RoundDownToEven(screenRegion.Height)));
    }

    private static int RoundDownToEven(int value) =>
        Math.Max(0, value) & ~1;
}
