using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Storage;
using WinDataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using WinDataRequestedEventArgs = Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs;
using WinDataTransferManager = Windows.ApplicationModel.DataTransfer.DataTransferManager;

namespace WinShot.History;

internal static class HistoryShareService
{
    internal const string BlipExecutableName = "blip.exe";
    private static readonly Guid DataTransferManagerId = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public static bool IsBlipInstalled() => IsBlipInstalled(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        File.Exists);

    internal static bool IsBlipInstalled(string? pathValue, string? localAppData, Func<string, bool> fileExists)
    {
        foreach (string directory in BlipSearchDirectories(pathValue, localAppData))
        {
            if (fileExists(Path.Combine(directory, BlipExecutableName)))
                return true;
        }
        return false;
    }

    internal static IReadOnlyList<string> BlipArguments(IEnumerable<string> paths) =>
        paths.SelectMany(static path => new[] { "--file", path }).ToArray();

    public static void ShareWithBlip(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var start = new ProcessStartInfo(BlipExecutableName) { UseShellExecute = true };
        foreach (string argument in BlipArguments(paths))
            start.ArgumentList.Add(argument);
        Process.Start(start);
    }

    public static bool IsWindowsShareSupported() => WinDataTransferManager.IsSupported();

    public static void ShowWindowsShare(Window owner, IReadOnlyList<string> paths, Action<Exception> onFailed)
        => ShowWindowsShare(new WindowInteropHelper(owner).EnsureHandle(), paths, onFailed);

    /// <summary>The share sheet for a plain HWND — the quick-actions card is a WinForms form.</summary>
    public static void ShowWindowsShare(IntPtr hwnd, IReadOnlyList<string> paths, Action<Exception> onFailed)
    {
        var interop = WinDataTransferManager.As<IDataTransferManagerInterop>();
        IntPtr result = interop.GetForWindow(hwnd, DataTransferManagerId);
        var manager = WinRT.MarshalInterface<WinDataTransferManager>.FromAbi(result);

        Windows.Foundation.TypedEventHandler<WinDataTransferManager, WinDataRequestedEventArgs>? handler = null;
        handler = async (_, args) =>
        {
            if (handler is not null)
                manager.DataRequested -= handler;

            var deferral = args.Request.GetDeferral();
            try
            {
                var data = args.Request.Data;
                data.Properties.Title = paths.Count == 1 ? "Share WinShot capture" : $"Share {paths.Count:N0} WinShot captures";
                data.Properties.Description = "Local files from WinShot History";
                data.RequestedOperation = WinDataPackageOperation.Copy;
                var files = new List<StorageFile>(paths.Count);
                foreach (string path in paths)
                    files.Add(await StorageFile.GetFileFromPathAsync(path));
                data.SetStorageItems(files);
            }
            catch (Exception ex)
            {
                onFailed(ex);
                args.Request.FailWithDisplayText("WinShot could not prepare these files for sharing.");
            }
            finally
            {
                deferral.Complete();
            }
        };

        manager.DataRequested += handler;
        interop.ShowShareUIForWindow(hwnd);
    }

    private static IEnumerable<string> BlipSearchDirectories(string? pathValue, string? localAppData)
    {
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return directory;
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "Microsoft", "WindowsApps");
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
