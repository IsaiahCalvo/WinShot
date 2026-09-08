using System.Collections.Specialized;
using System.IO;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>
/// Reads an image off the Windows clipboard. WinShot has always written to the clipboard and
/// never read from it, so opening a picture someone pasted you meant saving it to a file
/// first and opening that.
/// </summary>
public static class ClipboardImage
{
    /// <summary>
    /// Image formats in the order they are preferred. PNG first because the DIB path
    /// <see cref="WF.Clipboard.GetImage"/> uses loses the alpha channel, which turns a
    /// rounded-corner screenshot's transparent corners black.
    /// </summary>
    private static readonly string[] StreamFormats = ["PNG", "image/png"];

    /// <summary>
    /// The clipboard's image as a bitmap the caller owns, or null when there is no image on
    /// it. Runs on its own STA thread — the clipboard is an STA-only API and the callers are
    /// on a background worker.
    /// </summary>
    public static SD.Bitmap? TryRead()
    {
        SD.Bitmap? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = FromDataObject(WF.Clipboard.GetDataObject());
            }
            catch (Exception ex)
            {
                // Another process can hold the clipboard open, and a locked-down desktop can
                // refuse it outright. That is "nothing to open" for the user, not a crash.
                Log.Error("Reading an image from the clipboard failed", ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>
    /// Decodes the best image a clipboard payload carries, or null. Split out from
    /// <see cref="TryRead"/> so the format preference can be tested without a real clipboard
    /// — the hidden test desktop does not have one.
    /// </summary>
    internal static SD.Bitmap? FromDataObject(WF.IDataObject? data)
    {
        if (data is null)
            return null;

        foreach (string format in StreamFormats)
        {
            if (!SafeGetDataPresent(data, format))
                continue;
            if (SafeGetData(data, format) is not Stream stream)
                continue;
            if (Decode(() => stream, format) is SD.Bitmap decoded)
                return decoded;
        }

        if (SafeGetDataPresent(data, WF.DataFormats.Bitmap) &&
            SafeGetData(data, WF.DataFormats.Bitmap) is SD.Image image)
        {
            using (image)
                return new SD.Bitmap(image);
        }

        // An image file copied in Explorer is an image too, as far as anyone about to open it
        // in the editor is concerned.
        if (SafeGetDataPresent(data, WF.DataFormats.FileDrop) &&
            SafeGetData(data, WF.DataFormats.FileDrop) is object drop)
        {
            foreach (string path in FilePaths(drop))
            {
                if (!File.Exists(path))
                    continue;
                if (Decode(() => File.OpenRead(path), Path.GetFileName(path)) is SD.Bitmap fromFile)
                    return fromFile;
            }
        }

        return null;
    }

    private static IEnumerable<string> FilePaths(object drop) => drop switch
    {
        string[] array => array,
        StringCollection collection => collection.Cast<string?>().Where(p => p is not null).Select(p => p!),
        _ => Array.Empty<string>(),
    };

    private static SD.Bitmap? Decode(Func<Stream> open, string what)
    {
        try
        {
            using Stream stream = open();
            if (stream.CanSeek)
                stream.Position = 0;
            using var decoded = SD.Image.FromStream(stream);
            return new SD.Bitmap(decoded);
        }
        catch (Exception ex)
        {
            Log.Error($"Clipboard payload '{what}' is not a readable image; trying the next one", ex);
            return null;
        }
    }

    private static bool SafeGetDataPresent(WF.IDataObject data, string format)
    {
        try { return data.GetDataPresent(format); }
        catch (Exception ex)
        {
            Log.Error($"Clipboard could not be asked for '{format}'", ex);
            return false;
        }
    }

    private static object? SafeGetData(WF.IDataObject data, string format)
    {
        try { return data.GetData(format); }
        catch (Exception ex)
        {
            Log.Error($"Clipboard '{format}' data could not be fetched", ex);
            return null;
        }
    }
}
