using System.IO;
using System.Runtime.InteropServices;

namespace WinShot.Core;

/// <summary>
/// Turns an exception into a line a user can act on. Framework text is written for the
/// developer, not the person holding the mouse: a GDI+ save failure surfaced as
/// "Parameter is not valid.", a full disk as an HRESULT. Callers log the exception (with
/// its real message and stack) and show this instead.
/// </summary>
public static class FriendlyError
{
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    public static string For(Exception? ex) => ex switch
    {
        null => "Something went wrong.",
        UnauthorizedAccessException => "WinShot is not allowed to write there. Choose a different folder in Settings.",
        DirectoryNotFoundException => "That folder no longer exists. Choose a different folder in Settings.",
        PathTooLongException => "That file path is too long. Choose a shorter folder or file name.",
        IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull => "The drive is full.",
        IOException => "The file could not be written. It may be open in another app.",
        System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException
            => "WinShot couldn't reach the internet. Check your connection and try again.",
        OperationCanceledException or TimeoutException => "That took too long and was stopped.",
        OutOfMemoryException => "The image is too large to process on this PC.",
        // GDI+ reports both a bad image and a failed encode this way.
        ArgumentException or ExternalException => "The image could not be processed. Try the capture again.",
        _ => "Something went wrong. The details are in the log.",
    };
}
