using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Scrolling;

/// <summary>
/// Orchestrates the scrolling-capture UI: a CleanShot-style dim overlay that keeps the
/// captured region bright (so you see exactly what's being captured while you scroll the
/// live content under it), plus a small Cancel / Done bar next to that region. Done keeps
/// the stitched result; Cancel discards it; Esc (polled by the service) also finishes.
/// </summary>
public static class ScrollingStatusWindow
{
    /// <summary>
    /// Asks the user whether WinShot should auto-scroll or they will scroll themselves (and
    /// in which direction), shows the dim overlay + controls around <paramref name="screenRegion"/>
    /// (physical screen coordinates), runs the scrolling capture and tears the chrome down when
    /// done. Must be called on the UI thread. Returns the stitched bitmap (caller owns disposal),
    /// or null if nothing was captured, the chooser was cancelled, or the user pressed Cancel.
    /// </summary>
    public static async Task<SD.Bitmap?> Run(SD.Rectangle screenRegion, ScrollCaptureChoice? presetChoice = null)
    {
        ScrollCaptureChoice choice;
        if (presetChoice is ScrollCaptureChoice preset)
            choice = preset;
        else if (ScrollingModeDialog.Choose() is ScrollCaptureChoice picked)
            choice = picked;
        else
            return null; // cancelled at the mode chooser

        using var cts = new CancellationTokenSource();
        bool cancelled = false;

        var overlay = new ScrollDimOverlay(screenRegion);
        var controls = new ScrollControlsBar(screenRegion);
        controls.DoneRequested += () => { try { cts.Cancel(); } catch { /* already torn down */ } };
        controls.CancelRequested += () =>
        {
            cancelled = true;
            try { cts.Cancel(); } catch { /* already torn down */ }
        };

        try { overlay.Show(); }
        catch (Exception ex) { Log.Error("Scroll dim overlay failed to show (non-fatal)", ex); }
        controls.Show();

        try
        {
            SD.Bitmap? result = await ScrollingCaptureService.RunAsync(
                screenRegion,
                choice.Mode,
                choice.Direction,
                text => MarshalStatus(controls, text),
                cts.Token,
                preview: null); // the bright region is the live preview now

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
            try { overlay.Close(); overlay.Dispose(); } catch { /* best effort */ }
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
}
