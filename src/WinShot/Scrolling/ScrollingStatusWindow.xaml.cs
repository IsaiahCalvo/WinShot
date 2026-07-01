using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>
/// Orchestrates the scrolling-capture UI, mirroring CleanShot's flow: a dim overlay keeps
/// the captured region bright, a controls bar offers Start Capture / Auto-Scroll / Done /
/// Cancel, and a live preview panel shows the stitch growing. Capture is armed only when
/// the user clicks Start Capture (or Auto-Scroll, which starts with autopilot on), so they
/// can position the page first. Done keeps the stitched result; Cancel and Esc discard it.
/// </summary>
public static class ScrollingStatusWindow
{
    /// <summary>
    /// Shows the chrome around <paramref name="screenRegion"/> (physical screen coordinates),
    /// runs the scrolling capture and tears the chrome down when done. Must be called on the
    /// UI thread. Returns the stitched bitmap (caller owns disposal), or null when nothing was
    /// captured or the user cancelled. <paramref name="presetDirection"/> forces an axis
    /// (the scroll-horizontal command); null auto-detects from the first movement.
    /// </summary>
    public static async Task<SD.Bitmap?> Run(SD.Rectangle screenRegion, ScrollDirection? presetDirection = null)
    {
        using var cts = new CancellationTokenSource();
        bool cancelled = false;
        var autoFlag = new bool[1]; // written on the UI thread, read by the capture thread
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var overlay = new ScrollDimOverlay(screenRegion);
        var preview = new ScrollPreviewPanel(screenRegion);
        var controls = new ScrollControlsBar(screenRegion);
        controls.StartRequested += () => started.TrySetResult(true);
        controls.AutoScrollToggled += on => Volatile.Write(ref autoFlag[0], on);
        controls.DoneRequested += () => { try { cts.Cancel(); } catch { /* already torn down */ } };
        controls.CancelRequested += () =>
        {
            cancelled = true;
            started.TrySetResult(false);
            try { cts.Cancel(); } catch { /* already torn down */ }
        };

        try { overlay.Show(); }
        catch (Exception ex) { Log.Error("Scroll dim overlay failed to show (non-fatal)", ex); }
        try { preview.Show(); }
        catch (Exception ex) { Log.Error("Scroll preview failed to show (non-fatal)", ex); }
        controls.Show(); // shown last so the buttons sit above the overlay/preview

        try
        {
            // Wait for Start Capture / Auto-Scroll; Esc cancels while waiting.
            while (!started.Task.IsCompleted)
            {
                if ((GetAsyncKeyState(VkEscape) & 0x8000) != 0)
                {
                    cancelled = true;
                    break;
                }
                await Task.Delay(50);
            }
            if (cancelled || !await started.Task)
                return null;

            SD.Bitmap? result = await ScrollingCaptureService.RunAsync(
                screenRegion,
                presetDirection,
                () => Volatile.Read(ref autoFlag[0]),
                text => MarshalStatus(controls, text),
                hint => MarshalHint(controls, hint),
                cts.Token,
                thumb => PushPreview(preview, thumb));

            if (cancelled)
            {
                result?.Dispose();
                return null;
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Scrolling capture failed", ex);
            return null;
        }
        finally
        {
            try { controls.Close(); controls.Dispose(); } catch { /* best effort */ }
            try { preview.Close(); preview.Dispose(); } catch { /* best effort */ }
            try { overlay.Close(); overlay.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The live-stitch snapshot arrives on a capture thread with ownership transferred here;
    /// hand it to the preview panel (which takes ownership) on its UI thread.
    /// </summary>
    private static void PushPreview(ScrollPreviewPanel preview, SD.Bitmap thumb)
    {
        try
        {
            if (!preview.IsDisposed && preview.IsHandleCreated)
                preview.BeginInvoke(new Action(() => preview.SetImage(thumb, "Capturing…")));
            else
                thumb.Dispose();
        }
        catch
        {
            thumb.Dispose();
        }
    }

    /// <summary>Status arrives on a thread-pool thread; hop to the controls bar's UI thread.</summary>
    private static void MarshalStatus(ScrollControlsBar controls, string text)
    {
        try
        {
            if (controls.IsDisposed) return;
            if (controls.IsHandleCreated)
                controls.BeginInvoke(new Action(() => controls.SetStatus(text)));
        }
        catch
        {
            // The capture can end while a status update is in flight; ignore.
        }
    }

    /// <summary>Guidance hints from the capture thread; hop to the controls bar's UI thread.</summary>
    private static void MarshalHint(ScrollControlsBar controls, ScrollHint hint)
    {
        try
        {
            if (controls.IsDisposed) return;
            if (controls.IsHandleCreated)
                controls.BeginInvoke(new Action(() => controls.SetHint(hint)));
        }
        catch
        {
            // The capture can end while a hint toggle is in flight; ignore.
        }
    }

    private const int VkEscape = 0x1B;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
