using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>
/// Repeatedly captures a fixed screen region, sends wheel-scroll input to the
/// content under it, and stitches the frames into one tall image. The whole
/// loop runs on the thread pool; the status callback fires on a thread-pool
/// thread, so UI consumers must marshal via their Dispatcher.
/// </summary>
public static class ScrollingCaptureService
{
    private const int MaxIterations = 60;
    private const int MaxStitchedHeight = 32000;
    private const int ScrollSettleMs = 400;
    private const int WheelDelta = 120;
    private const int ScrollNotchesPerStep = -3; // negative = scroll down (content moves up)

    /// <summary>
    /// Runs the auto-scroll capture for <paramref name="screenRegion"/> (physical screen
    /// coordinates). Stops at page bottom (no movement detected twice in a row), on
    /// cancellation, on Esc, after 60 iterations, or at 32000px stitched height — and
    /// returns whatever was stitched so far (null only if nothing was captured).
    /// </summary>
    public static async Task<SD.Bitmap?> RunAsync(SD.Rectangle screenRegion, Action<string> status, CancellationToken ct)
    {
        if (screenRegion.Width < 1 || screenRegion.Height < 1)
            return null;

        return await Task.Run(async () =>
        {
            SD.Bitmap? stitched = null;
            SD.Bitmap? previous = null;
            try
            {
                int frames = 0;
                int zeroOffsetStreak = 0;

                for (int i = 0; i < MaxIterations; i++)
                {
                    if (ShouldStop(ct))
                        break;

                    var frame = CaptureService.CaptureScreenRegion(screenRegion);
                    frames++;

                    if (stitched is null)
                    {
                        stitched = frame;
                        previous = (SD.Bitmap)frame.Clone();
                    }
                    else
                    {
                        int offset = ImageStitcher.FindScrollOffset(previous!, frame);
                        if (offset == 0)
                        {
                            zeroOffsetStreak++;
                        }
                        else
                        {
                            zeroOffsetStreak = 0;
                            int rows = Math.Min(offset, MaxStitchedHeight - stitched.Height);
                            var grown = ImageStitcher.AppendBelow(stitched, frame, rows);
                            stitched.Dispose();
                            stitched = grown;
                        }

                        previous!.Dispose();
                        previous = frame;

                        if (zeroOffsetStreak >= 2)
                            break; // page bottom reached
                        if (stitched.Height >= MaxStitchedHeight)
                            break;
                    }

                    status($"Captured {frames} frames - {stitched.Height}px");

                    if (ShouldStop(ct))
                        break;

                    ScrollDown(screenRegion);
                    try
                    {
                        await Task.Delay(ScrollSettleMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Scrolling capture failed mid-run; returning partial result", ex);
            }
            finally
            {
                previous?.Dispose();
            }
            return stitched;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool ShouldStop(CancellationToken ct) =>
        ct.IsCancellationRequested || (GetAsyncKeyState(VkEscape) & 0x8000) != 0;

    /// <summary>Moves the cursor to the region center and sends one wheel-down step.</summary>
    private static void ScrollDown(SD.Rectangle region)
    {
        SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
        var input = new INPUT
        {
            type = InputMouse,
            mi = new MOUSEINPUT
            {
                mouseData = unchecked((uint)(WheelDelta * ScrollNotchesPerStep)),
                dwFlags = MouseEventFWheel,
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private const int VkEscape = 0x1B;
    private const uint InputMouse = 0;
    private const uint MouseEventFWheel = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // MOUSEINPUT is the largest member of the INPUT union, so declaring only it
    // keeps the marshalled size correct for SendInput.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
