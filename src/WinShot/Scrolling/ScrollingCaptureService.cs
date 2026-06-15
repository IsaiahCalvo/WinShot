using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Scrolling;

/// <summary>
/// Repeatedly captures a fixed screen region and stitches the frames into one
/// tall image (or one wide image for <see cref="ScrollDirection.Horizontal"/>).
/// In <see cref="ScrollCaptureMode.Auto"/> it also sends wheel-scroll input to
/// the content under the region; in <see cref="ScrollCaptureMode.Manual"/>
/// it only watches while the user scrolls themselves. The whole loop runs on the
/// thread pool; the status callback fires on a thread-pool thread, so UI consumers
/// must marshal via their Dispatcher.
/// </summary>
public static class ScrollingCaptureService
{
    private const int MaxIterations = 60;
    private const int MaxStitchedHeight = 32000;
    private const int MaxStitchedWidth = 32000;
    private const int ScrollSettleMs = 400;
    private const int WheelDelta = 120;
    private const int ScrollNotchesPerStep = -3; // negative = scroll down (content moves up)
    private const int HorizontalScrollNotchesPerStep = 3; // positive = scroll right (content moves left)

    /// <summary>Manual mode: how often the region is re-captured and checked for movement.</summary>
    private const int ManualPollMs = 300;

    /// <summary>Manual mode: generous overall cap so an abandoned capture eventually ends.</summary>
    private const int ManualTimeoutMs = 10 * 60 * 1000;

    /// <summary>
    /// Runs a scrolling capture for <paramref name="screenRegion"/> (physical screen
    /// coordinates) and returns whatever was stitched so far (null only if nothing
    /// was captured). <paramref name="direction"/> picks the axis: vertical grows the
    /// stitch downward, horizontal grows it rightward (capped at 32000px width).
    /// Auto mode: sends wheel-scroll input (vertical wheel, or horizontal wheel for
    /// horizontal captures) and stops at the content end (no movement detected twice
    /// in a row), on cancellation, on Esc, after 60 iterations, or at the 32000px
    /// stitched height/width cap.
    /// Manual mode: never scrolls or moves the cursor — it just re-captures every
    /// ~300 ms and appends whenever forward (down/right) movement is detected. Pauses
    /// are fine (zero offset never ends the capture); backward scrolls are ignored.
    /// Ends only on cancellation, Esc, the 32000px cap, or a 10-minute timeout.
    /// </summary>
    public static async Task<SD.Bitmap?> RunAsync(SD.Rectangle screenRegion, ScrollCaptureMode mode,
        ScrollDirection direction, Action<string> status, CancellationToken ct)
    {
        if (screenRegion.Width < 1 || screenRegion.Height < 1)
            return null;

        bool manual = mode == ScrollCaptureMode.Manual;
        bool horizontal = direction == ScrollDirection.Horizontal;

        return await Task.Run(async () =>
        {
            SD.Bitmap? stitched = null;
            SD.Bitmap? previous = null;
            try
            {
                int frames = 0;
                int stitchedFrames = 0;
                int zeroOffsetStreak = 0;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; manual ? elapsed.ElapsedMilliseconds < ManualTimeoutMs : i < MaxIterations; i++)
                {
                    if (ShouldStop(ct))
                        break;

                    var frame = CaptureService.CaptureScreenRegion(screenRegion);
                    frames++;

                    if (stitched is null)
                    {
                        stitched = frame;
                        previous = (SD.Bitmap)frame.Clone();
                        stitchedFrames = 1;
                    }
                    else
                    {
                        int offset = horizontal
                            ? ImageStitcher.FindScrollOffsetHorizontal(previous!, frame)
                            : ImageStitcher.FindScrollOffset(previous!, frame);
                        if (offset == 0)
                        {
                            zeroOffsetStreak++;
                            if (manual)
                            {
                                // Keep `previous` anchored to the frame whose bottom row (or
                                // rightmost column) matches the stitch's edge. This makes
                                // backward scrolls (which the forward-only offset search
                                // reports as 0) harmless: once the user scrolls back past the
                                // stitched edge, the next offset is computed against the
                                // anchor and appends only truly new content.
                                frame.Dispose();
                            }
                            else
                            {
                                previous!.Dispose();
                                previous = frame;
                            }
                        }
                        else
                        {
                            zeroOffsetStreak = 0;
                            SD.Bitmap grown;
                            if (horizontal)
                            {
                                int cols = Math.Min(offset, MaxStitchedWidth - stitched.Width);
                                grown = ImageStitcher.AppendRight(stitched, frame, cols);
                            }
                            else
                            {
                                int rows = Math.Min(offset, MaxStitchedHeight - stitched.Height);
                                grown = ImageStitcher.AppendBelow(stitched, frame, rows);
                            }
                            stitched.Dispose();
                            stitched = grown;
                            stitchedFrames++;
                            previous!.Dispose();
                            previous = frame;
                        }

                        if (!manual && zeroOffsetStreak >= 2)
                            break; // content end reached
                        if (horizontal ? stitched.Width >= MaxStitchedWidth : stitched.Height >= MaxStitchedHeight)
                            break;
                    }

                    string extent = horizontal ? $"{stitched.Width}px wide" : $"{stitched.Height}px";
                    status(manual
                        ? $"Scroll the content yourself — click Stop when done. {stitchedFrames} frames – {extent}"
                        : $"Captured {frames} frames - {extent}");

                    if (ShouldStop(ct))
                        break;

                    if (!manual)
                    {
                        if (horizontal)
                            ScrollRight(screenRegion);
                        else
                            ScrollDown(screenRegion);
                    }
                    try
                    {
                        await Task.Delay(manual ? ManualPollMs : ScrollSettleMs, ct).ConfigureAwait(false);
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

    /// <summary>Moves the cursor to the region center and sends one horizontal wheel-right step.</summary>
    private static void ScrollRight(SD.Rectangle region)
    {
        SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
        var input = new INPUT
        {
            type = InputMouse,
            mi = new MOUSEINPUT
            {
                mouseData = (uint)(WheelDelta * HorizontalScrollNotchesPerStep),
                dwFlags = MouseEventFHWheel,
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private const int VkEscape = 0x1B;
    private const uint InputMouse = 0;
    private const uint MouseEventFWheel = 0x0800;
    private const uint MouseEventFHWheel = 0x1000;

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
