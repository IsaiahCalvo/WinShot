using System.Drawing;
using System.IO;

namespace WinShot.History;

internal static class HistoryImageLoader
{
    /// <summary>
    /// GDI+ bitmaps created directly from a stream can keep using that stream lazily.
    /// Clone the decoded image before the file stream is disposed so clipboard work remains valid.
    /// </summary>
    public static Bitmap LoadDetachedBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }
}
