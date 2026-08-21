using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace WinShot.Core;

/// <summary>
/// Asks Windows to let this process capture without the yellow "display is being captured"
/// rectangle it otherwise paints around the monitor. Setting
/// <see cref="GraphicsCaptureSession.IsBorderRequired"/> only takes effect once the process
/// holds this grant, so it is requested once at startup and the result cached.
///
/// Windows 11 21H2 (build 22000) and up; a no-op everywhere else, and a denial is harmless —
/// capture still works, the border just stays.
/// </summary>
internal static class BorderlessCaptureAccess
{
    private static Task<bool>? _request;

    /// <summary>Kicks the request off in the background; safe to call more than once.</summary>
    public static void RequestOnce()
    {
        if (_request is not null) return;
        _request = RequestAsync();
    }

    private static async Task<bool> RequestAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return false;

        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            bool granted = status == AppCapabilityAccessStatus.Allowed;
            Log.Info($"Borderless capture access: {status}");
            return granted;
        }
        catch (Exception ex)
        {
            Log.Info($"Borderless capture access unavailable: {ex.Message}");
            return false;
        }
    }
}
