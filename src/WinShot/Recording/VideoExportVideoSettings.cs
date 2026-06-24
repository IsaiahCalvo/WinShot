namespace WinShot.Recording;

public readonly record struct VideoExportVideoSettings(
    uint Width,
    uint Height,
    uint Bitrate,
    double FrameRate)
{
    public static VideoExportVideoSettings FromControls(
        uint sourceWidth,
        uint sourceHeight,
        double sourceFrameRate,
        int resolutionIndex,
        int qualityIndex,
        int frameRateIndex)
    {
        uint srcW = sourceWidth == 0 ? 1920u : sourceWidth;
        uint srcH = sourceHeight == 0 ? 1080u : sourceHeight;
        double scale = resolutionIndex switch { 1 => 0.75, 2 => 0.5, _ => 1.0 };
        uint width = (uint)Math.Max(2, (int)Math.Round(srcW * scale) & ~1);
        uint height = (uint)Math.Max(2, (int)Math.Round(srcH * scale) & ~1);
        double frameRate = frameRateIndex switch
        {
            1 => 60,
            2 => 30,
            3 => 15,
            _ => CleanSourceFrameRate(sourceFrameRate),
        };
        double bitsPerPixel = qualityIndex switch { 1 => 0.08, 2 => 0.045, _ => 0.13 };
        uint bitrate = (uint)Math.Clamp(width * height * frameRate * bitsPerPixel, 500_000, 60_000_000);

        return new VideoExportVideoSettings(width, height, bitrate, frameRate);
    }

    private static double CleanSourceFrameRate(double frameRate) =>
        double.IsFinite(frameRate) && frameRate > 0 && frameRate <= 120 ? frameRate : 30;
}
