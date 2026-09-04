using System.ComponentModel;
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
    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    public static string For(Exception? ex) => ex switch
    {
        null => "Something went wrong.",
        UnauthorizedAccessException => "WinShot is not allowed to write there. Choose a different folder in Settings.",
        DirectoryNotFoundException => "That folder no longer exists. Choose a different folder in Settings.",
        PathTooLongException => "That file path is too long. Choose a shorter folder or file name.",
        IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull => "The drive is full.",
        // FriendlyError runs on read paths too (opening a background image, deleting a
        // history file), so the generic line must not claim a write was attempted.
        FileNotFoundException => "That file is no longer there.",
        IOException => "Windows could not read or write that file. It may be open in another app.",
        System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException
            => "WinShot couldn't reach the internet. Check your connection and try again.",
        // HttpClient reports a request timeout as a TaskCanceledException wrapping a
        // TimeoutException, so read the inner one before telling a user who waited through
        // a network stall that they cancelled it.
        OperationCanceledException { InnerException: TimeoutException } or TimeoutException
            => "That took too long and was stopped.",
        // A deliberate cancel is not a timeout: TaskCanceledException derives from
        // OperationCanceledException, and the recording and export paths both throw it.
        OperationCanceledException => "That was cancelled.",
        OutOfMemoryException => "The image is too large to process on this PC.",
        // CLIPBRD_E_CANT_OPEN: another app owns the clipboard. The one COM failure with an
        // answer better than "check the log", and the copy paths hit it regularly.
        COMException { HResult: ClipboardCantOpen } => "Another app is holding the clipboard. Try again in a moment.",
        // GDI+ reports both a bad image and a failed encode this way - as the base types
        // exactly, never as a derived one. The derived types are not about images at all:
        // Win32Exception is a failed Process.Start or a denied API call, and
        // ArgumentNullException/ArgumentOutOfRangeException are ordinary bugs. Matching the
        // bases loosely told a user whose shortcut target had been uninstalled that
        // "the image could not be processed", on a path with no image in it.
        // COMException derives from ExternalException too, and a COM/WinRT failure on a video
        // stop or a history delete has no image in it — it was being told to retry the capture.
        ExternalException and not (Win32Exception or COMException)
            => "The image could not be processed. Try the capture again.",
        ArgumentException when ex.GetType() == typeof(ArgumentException)
            => "The image could not be processed. Try the capture again.",
        _ => "Something went wrong. The details are in the log.",
    };
}
