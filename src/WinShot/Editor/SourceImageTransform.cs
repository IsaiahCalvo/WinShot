using SD = System.Drawing;

namespace WinShot.Editor;

public static class SourceImageTransform
{
    public static SD.Bitmap RotateFlip(SD.Bitmap source, SD.RotateFlipType type)
    {
        var copy = new SD.Bitmap(source);
        copy.RotateFlip(type);
        return copy;
    }
}
