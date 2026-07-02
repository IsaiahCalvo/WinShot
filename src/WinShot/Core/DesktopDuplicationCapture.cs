using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using WF = System.Windows.Forms;

namespace WinShot.Core;

internal static class DesktopDuplicationCapture
{
    private const int FirstFrameTimeoutMs = 20;
    private const int FailureCooldownMs = 30_000;
    private const int CaptureAttemptTimeoutMs = 2_500;
    // Background probes run off the user path, so they can afford a generous allowance —
    // see WindowsGraphicsCaptureCapture.ProbeTimeoutMs.
    private const int ProbeTimeoutMs = 10_000;
    private const int AccessDeniedCooldownMs = 10 * 60 * 1000;
    private const uint DxgiErrorWaitTimeout = 0x887A0027;
    private const uint HResultAccessDenied = 0x80070005;
    private const int IdleEvictMs = 30_000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DisplayCapture> Displays = new(StringComparer.OrdinalIgnoreCase);
    private static long _disabledUntilTick;
    private static long _lastUseTick;
    private static int _evictionScheduled;
    // Starts in probation (1) — see WindowsGraphicsCaptureCapture._lastAttemptFailed.
    private static int _lastAttemptFailed = 1;
    private static int _probeActive;
    private static int _attemptInFlight;

    public static bool TryCaptureRegion(Rectangle screenRect, out Bitmap? bitmap)
    {
        bitmap = null;
        if (IsTemporarilyDisabled())
            return false;

        // Circuit breaker — see WindowsGraphicsCaptureCapture.TryCaptureRegion: after any
        // failure, captures drop straight to BitBlt (instant) and a background probe
        // re-enables duplication once a healthy capture is observed.
        if (Volatile.Read(ref _lastAttemptFailed) != 0)
        {
            StartBackgroundProbe(screenRect);
            return false;
        }

        return TryCaptureBounded(screenRect, CaptureAttemptTimeoutMs, out bitmap);
    }

    private static bool TryCaptureBounded(Rectangle screenRect, int timeoutMs, out Bitmap? bitmap)
    {
        bitmap = null;

        // At most one attempt worker — see WindowsGraphicsCaptureCapture.TryCaptureBounded.
        if (Interlocked.CompareExchange(ref _attemptInFlight, 1, 0) != 0)
            return false;

        // Same GPU-contention hazard as Windows Graphics Capture: DXGI duplication setup
        // can stall for minutes (not fail) while remote-control software holds the capture
        // path. Bound the attempt so callers drop to BitBlt; the stalled attempt cleans
        // itself up whenever the native call finally returns.
        Task<Bitmap?> attempt = Task.Run(() =>
        {
            try
            {
                return CaptureRegionGated(screenRect);
            }
            finally
            {
                Interlocked.Exchange(ref _attemptInFlight, 0);
            }
        });
        bool finished;
        try
        {
            finished = attempt.Wait(timeoutMs);
        }
        catch (AggregateException)
        {
            // CaptureRegionGated already logged and started the cooldown.
            Volatile.Write(ref _lastAttemptFailed, 1);
            return false;
        }

        if (finished)
        {
            bitmap = attempt.Result;
            if (bitmap is null)
                Volatile.Write(ref _lastAttemptFailed, 1);
            return bitmap is not null;
        }

        // Cooldown first, then the flag — see WindowsGraphicsCaptureCapture.TryCaptureBounded.
        DisableTemporarily(FailureCooldownMs);
        Volatile.Write(ref _lastAttemptFailed, 1);
        Log.Info(
            $"Desktop duplication still starting after {timeoutMs} ms; " +
            $"using GDI fallback and skipping duplication for {FailureCooldownMs / 1000} s.");
        attempt.ContinueWith(
            t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    t.Result?.Dispose();
                else
                    _ = t.Exception; // observe so an abandoned attempt can't surface later
            },
            TaskScheduler.Default);
        return false;
    }

    private static void StartBackgroundProbe(Rectangle screenRect)
    {
        if (Interlocked.CompareExchange(ref _probeActive, 1, 0) != 0)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                if (TryCaptureBounded(screenRect, ProbeTimeoutMs, out Bitmap? probe))
                {
                    probe!.Dispose();
                    Volatile.Write(ref _lastAttemptFailed, 0);
                    Log.Info("Desktop duplication probe succeeded; re-enabling duplication for captures.");
                }
                else if (!IsTemporarilyDisabled())
                {
                    // Throttle probes to one per cooldown window — but never shorten a
                    // cooldown the failure path already armed (e.g. the longer
                    // access-denied back-off).
                    DisableTemporarily(FailureCooldownMs);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Desktop duplication probe failed", ex);
                if (!IsTemporarilyDisabled())
                    DisableTemporarily(FailureCooldownMs);
            }
            finally
            {
                Interlocked.Exchange(ref _probeActive, 0);
            }
        });
    }

    private static Bitmap? CaptureRegionGated(Rectangle screenRect)
    {
        lock (Gate)
        {
            try
            {
                Bitmap bitmap = CaptureRegionCore(screenRect);
                Interlocked.Exchange(ref _lastUseTick, Environment.TickCount64);
                ScheduleIdleEviction();
                return bitmap;
            }
            catch (Exception ex)
            {
                if (IsAccessDenied(ex))
                    Log.Info("Desktop duplication unavailable; using GDI capture fallback.");
                else if (!IsWaitTimeout(ex))
                    Log.Error("Desktop duplication capture failed; falling back to GDI", ex);
                DisableTemporarily(ex);
                Reset();
                return null;
            }
        }
    }

    // Probation counts as unavailable so CaptureService picks its fast BitBlt branch
    // instead of the slower CAPTUREBLT path while duplication is sidelined.
    public static bool IsTemporarilyUnavailable =>
        IsTemporarilyDisabled() || Volatile.Read(ref _lastAttemptFailed) != 0;

    public static void ReleaseResources()
    {
        // Best-effort: a stalled attempt can hold the gate for minutes inside a native
        // call — never block app shutdown on it; the abandoned worker cleans up via
        // Reset() in its own failure path whenever the native call returns.
        if (!Monitor.TryEnter(Gate, 250))
            return;
        try
        {
            Reset();
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    private static bool IsTemporarilyDisabled() =>
        Environment.TickCount64 < Interlocked.Read(ref _disabledUntilTick);

    private static void DisableTemporarily(Exception ex) =>
        DisableTemporarily(IsAccessDenied(ex) ? AccessDeniedCooldownMs : FailureCooldownMs);

    private static void DisableTemporarily(int cooldownMs) =>
        Interlocked.Exchange(ref _disabledUntilTick, Environment.TickCount64 + cooldownMs);

    private static bool IsWaitTimeout(Exception ex) =>
        unchecked((uint)ex.HResult) == DxgiErrorWaitTimeout ||
        ex.Message.Contains("DXGI_ERROR_WAIT_TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("WaitTimeout", StringComparison.OrdinalIgnoreCase);

    private static bool IsAccessDenied(Exception ex) =>
        unchecked((uint)ex.HResult) == HResultAccessDenied ||
        ex.Message.Contains("E_ACCESSDENIED", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);

    private static Bitmap CaptureRegionCore(Rectangle screenRect)
    {
        if (screenRect.Width < 1 || screenRect.Height < 1)
            throw new ArgumentException("Capture region is empty.", nameof(screenRect));

        var screens = WF.Screen.AllScreens
            .Where(s =>
            {
                Rectangle overlap = Rectangle.Intersect(s.Bounds, screenRect);
                return overlap.Width > 0 && overlap.Height > 0;
            })
            .ToArray();
        if (screens.Length == 0)
            throw new InvalidOperationException("Capture region does not overlap any display.");

        var result = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppRgb);
        BitmapData? targetData = null;
        bool disposeResult = false;
        try
        {
            targetData = result.LockBits(
                new Rectangle(0, 0, result.Width, result.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppRgb);

            foreach (var screen in screens)
            {
                Rectangle intersection = Rectangle.Intersect(screen.Bounds, screenRect);
                if (intersection.Width < 1 || intersection.Height < 1)
                    continue;

                DisplayCapture display = GetDisplay(screen);
                display.CopyInto(
                    targetData,
                    screenRect,
                    screen.Bounds,
                    intersection);
            }

            return result;
        }
        catch
        {
            disposeResult = true;
            throw;
        }
        finally
        {
            if (targetData is not null)
                result.UnlockBits(targetData);
            if (disposeResult)
                result.Dispose();
        }
    }

    private static DisplayCapture GetDisplay(WF.Screen screen)
    {
        if (Displays.TryGetValue(screen.DeviceName, out var existing) &&
            existing.ScreenBounds == screen.Bounds)
        {
            return existing;
        }

        existing?.Dispose();
        var created = new DisplayCapture(screen);
        Displays[screen.DeviceName] = created;
        return created;
    }

    private static void Reset()
    {
        foreach (var display in Displays.Values)
            display.Dispose();
        Displays.Clear();
    }

    /// <summary>
    /// Disposes the cached D3D device + duplication interface ~30s after the last capture so a
    /// tray-resident WinShot doesn't hold tens of MB of GPU/driver memory open forever (it
    /// previously freed these only on app exit). One-shot delay, not a polling timer, so it
    /// adds zero idle CPU; the next capture simply recreates the display via GetDisplay.
    /// </summary>
    private static void ScheduleIdleEviction()
    {
        if (Interlocked.Exchange(ref _evictionScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(IdleEvictMs).ConfigureAwait(false);
            lock (Gate)
            {
                Interlocked.Exchange(ref _evictionScheduled, 0);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastUseTick) >= IdleEvictMs)
                    Reset();
                else if (Displays.Count > 0)
                    ScheduleIdleEviction();
            }
        });
    }

    private sealed class DisplayCapture : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private readonly ID3D11Texture2D _staging;
        private readonly int _width;
        private readonly int _height;
        private bool _hasFrame;

        public DisplayCapture(WF.Screen screen)
        {
            ScreenBounds = screen.Bounds;

            D3D11CreateDevice(
                null!,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                null!,
                out _device,
                out FeatureLevel _,
                out _context).CheckError();

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var output = FindOutput(adapter, screen.DeviceName);
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);

            var desc = _duplication.Description;
            _width = (int)desc.ModeDescription.Width;
            _height = (int)desc.ModeDescription.Height;
            if (_width != screen.Bounds.Width || _height != screen.Bounds.Height)
                throw new InvalidOperationException(
                    $"Display duplication size {_width}x{_height} does not match screen bounds {screen.Bounds.Width}x{screen.Bounds.Height}.");

            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.ModeDescription.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        public Rectangle ScreenBounds { get; }

        public void CopyInto(
            BitmapData target,
            Rectangle targetRegion,
            Rectangle screenBounds,
            Rectangle intersection)
        {
            RefreshFrameIfAvailable();

            _context.Map(
                _staging,
                0,
                MapMode.Read,
                Vortice.Direct3D11.MapFlags.None,
                out MappedSubresource mapped).CheckError();
            try
            {
                int sourceX = intersection.X - screenBounds.X;
                int sourceY = intersection.Y - screenBounds.Y;
                int targetX = intersection.X - targetRegion.X;
                int targetY = intersection.Y - targetRegion.Y;
                nuint rowBytes = (nuint)(intersection.Width * 4);

                for (int y = 0; y < intersection.Height; y++)
                {
                    IntPtr sourceRow = IntPtr.Add(
                        mapped.DataPointer,
                        (sourceY + y) * (int)mapped.RowPitch + sourceX * 4);
                    IntPtr targetRow = IntPtr.Add(
                        target.Scan0,
                        (targetY + y) * target.Stride + targetX * 4);
                    CopyMemory(targetRow, sourceRow, rowBytes);
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }

        private void RefreshFrameIfAvailable()
        {
            IDXGIResource? resource = null;
            bool acquired = false;
            try
            {
                var result = _duplication.AcquireNextFrame(
                    _hasFrame ? 0u : FirstFrameTimeoutMs,
                    out OutduplFrameInfo frameInfo,
                    out resource);

                if (result.Failure)
                {
                    if (_hasFrame)
                        return;
                    result.CheckError();
                }

                acquired = true;

                // A pointer-only update (LastPresentTime == 0) acquires successfully but the
                // duplication's desktop-image surface has never received a present — staging
                // it would return an all-black frame as a "successful" capture. Only possible
                // before the first real frame; treat it like a first-frame wait timeout so the
                // caller falls through to BitBlt.
                if (!_hasFrame && frameInfo.LastPresentTime == 0)
                {
                    throw new InvalidOperationException(
                        "First duplication frame was a pointer-only update with no desktop image (DXGI_ERROR_WAIT_TIMEOUT).")
                    {
                        HResult = unchecked((int)DxgiErrorWaitTimeout),
                    };
                }

                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                _context.CopyResource(_staging, texture);
                _hasFrame = true;
            }
            finally
            {
                resource?.Dispose();
                if (acquired)
                    _duplication.ReleaseFrame();
            }
        }

        public void Dispose()
        {
            _staging.Dispose();
            _duplication.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }

    private static IDXGIOutput FindOutput(IDXGIAdapter adapter, string deviceName)
    {
        for (uint i = 0; ; i++)
        {
            adapter.EnumOutputs(i, out IDXGIOutput output);
            if (output is null)
                throw new InvalidOperationException($"Display output not found: {deviceName}");

            if (string.Equals(output.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return output;

            output.Dispose();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, nuint length);
}
