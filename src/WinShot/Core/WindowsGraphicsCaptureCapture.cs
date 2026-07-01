using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using WF = System.Windows.Forms;

namespace WinShot.Core;

internal static class WindowsGraphicsCaptureCapture
{
    private const int FirstFrameTimeoutMs = 1000;
    private const int FailureCooldownMs = 30_000;
    private const uint HResultAccessDenied = 0x80070005;
    private const uint HResultNoInterface = 0x80004002;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly object Gate = new();
    // Serializes all ID3D11DeviceContext (immediate context) access. Frame pools are
    // free-threaded, so FrameArrived can fire on arbitrary threads for several displays
    // that share one device/context; the immediate context is NOT thread-safe.
    private static readonly object ContextLock = new();
    private static readonly Dictionary<string, GraphicsCaptureItem> ItemCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DisplayCapture> Displays = new(StringComparer.OrdinalIgnoreCase);
    private static DeviceResources? _deviceResources;
    private static long _disabledUntilTick;

    public static bool TryCaptureRegion(Rectangle screenRect, out Bitmap? bitmap)
    {
        bitmap = null;
        if (!IsSupported() || IsTemporarilyDisabled())
            return false;

        lock (Gate)
        {
            var total = Stopwatch.StartNew();
            try
            {
                bitmap = CaptureRegionCore(screenRect);
                if (total.ElapsedMilliseconds > 50)
                {
                    Log.Info(
                        "Perf WGC capture: " +
                        $"total={total.ElapsedMilliseconds} ms size={screenRect.Width}x{screenRect.Height}");
                }

                return true;
            }
            catch (Exception ex)
            {
                if (IsExpectedUnavailable(ex))
                    Log.Info($"Windows Graphics Capture unavailable; using GDI fallback. {ex.Message}");
                else
                    Log.Error("Windows Graphics Capture failed; falling back to GDI", ex);

                ResetDeviceResources();
                DisableTemporarily();
                return false;
            }
            // Single-shot by design: never keep a WGC session alive between captures.
            // A live session shows a yellow capture border and does idle GPU work, so we
            // fully tear down device + sessions after every capture (see Prewarm comment).
            finally
            {
                ResetDeviceResources();
            }
        }
    }

    public static void Prewarm(Rectangle screenRect)
    {
        // Do not keep a WGC session open just to warm the path. Windows shows
        // monitor capture sessions with a yellow border while they are alive.
    }

    public static void ReleaseResources()
    {
        lock (Gate)
        {
            ResetDeviceResources();
        }
    }

    private static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTemporarilyDisabled() =>
        Environment.TickCount64 < Interlocked.Read(ref _disabledUntilTick);

    private static void DisableTemporarily() =>
        Interlocked.Exchange(ref _disabledUntilTick, Environment.TickCount64 + FailureCooldownMs);

    private static bool IsExpectedUnavailable(Exception ex)
    {
        uint hresult = unchecked((uint)ex.HResult);
        return hresult is HResultAccessDenied or HResultNoInterface ||
               ex is TimeoutException ||
               ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("E_ACCESSDENIED", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("No frame arrived", StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceResources GetDeviceResources()
    {
        if (_deviceResources is not null)
            return _deviceResources;

        _deviceResources = new DeviceResources();
        return _deviceResources;
    }

    private static void ResetDeviceResources()
    {
        foreach (var display in Displays.Values)
            display.Dispose();
        Displays.Clear();
        // GraphicsCaptureItem is a WinRT object (not IDisposable); dropping the references
        // lets GC release the underlying COM handles. Cleared every capture so items never
        // accumulate across captures.
        ItemCache.Clear();
        _deviceResources?.Dispose();
        _deviceResources = null;
    }

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
        var createDisplaysMs = 0L;
        var frameMs = 0L;
        var copyMs = 0L;
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

                var create = Stopwatch.StartNew();
                var display = GetDisplay(screen);
                createDisplaysMs += create.ElapsedMilliseconds;
                display.CopyInto(targetData, screenRect, screen.Bounds, intersection, out long displayFrameMs, out long displayCopyMs);
                frameMs += displayFrameMs;
                copyMs += displayCopyMs;
            }

            long measuredMs = createDisplaysMs + frameMs + copyMs;
            if (measuredMs > 50)
            {
                Log.Info(
                    "Perf WGC breakdown: " +
                    $"setup={createDisplaysMs} frame={frameMs} copy={copyMs} " +
                    $"measured={measuredMs} ms size={screenRect.Width}x{screenRect.Height}");
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
        string key = GetScreenKey(screen);
        if (Displays.TryGetValue(key, out var existing))
            return existing;

        var created = new DisplayCapture(screen);
        Displays[key] = created;
        return created;
    }

    private static string GetScreenKey(WF.Screen screen) =>
        $"{screen.DeviceName}|{screen.Bounds.X},{screen.Bounds.Y},{screen.Bounds.Width},{screen.Bounds.Height}";

    private sealed class DisplayCapture : IDisposable
    {
        private readonly DeviceResources _resources;
        private readonly Direct3D11CaptureFramePool _framePool;
        private readonly GraphicsCaptureSession _session;
        private readonly object _frameGate = new();
        private readonly GraphicsCaptureItem _item;
        private ID3D11Texture2D? _staging;
        private Bitmap? _latestBitmap;
        private bool _disposed;

        public DisplayCapture(WF.Screen screen)
        {
            _resources = GetDeviceResources();
            _item = GetItemForScreen(screen);
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _resources.WinRtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(_item);
            TryConfigureSession(_session);
            _session.StartCapture();
        }

        public void CopyInto(
            BitmapData target,
            Rectangle targetRegion,
            Rectangle screenBounds,
            Rectangle intersection,
            out long frameMs,
            out long copyMs)
        {
            var frameWatch = Stopwatch.StartNew();
            lock (_frameGate)
            {
                Bitmap latest = GetLatestBitmapForRead();
                frameMs = frameWatch.ElapsedMilliseconds;

                var copyWatch = Stopwatch.StartNew();
                if (latest.Width != screenBounds.Width || latest.Height != screenBounds.Height)
                {
                    throw new InvalidOperationException(
                        $"WGC frame size {latest.Width}x{latest.Height} does not match screen bounds {screenBounds.Width}x{screenBounds.Height}.");
                }

                CopyBitmapIntoTarget(latest, target, targetRegion, screenBounds, intersection);
                copyMs = copyWatch.ElapsedMilliseconds;
            }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is null)
                    return;

                lock (_frameGate)
                {
                    if (_disposed)
                        return;

                    Bitmap nextBitmap = CreateBitmapFromFrame(frame);
                    Bitmap? previous = _latestBitmap;
                    _latestBitmap = nextBitmap;
                    Monitor.PulseAll(_frameGate);
                    previous?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Windows Graphics Capture frame arrival failed", ex);
            }
        }

        private Bitmap GetLatestBitmapForRead()
        {
            if (_latestBitmap is null)
                Monitor.Wait(_frameGate, FirstFrameTimeoutMs);

            if (_latestBitmap is null)
                throw new TimeoutException("No frame arrived from Windows Graphics Capture.");

            return _latestBitmap;
        }

        private Bitmap CreateBitmapFromFrame(Direct3D11CaptureFrame frame)
        {
            using ID3D11Texture2D texture = GetTextureFromSurface(frame.Surface);
            Texture2DDescription desc = texture.Description;
            // CreateTexture2D on the device is free-threaded; only the immediate context
            // (CopyResource/Map/Unmap below) needs serializing across displays.
            ID3D11Texture2D staging = GetOrCreateStaging(desc);

            lock (ContextLock)
            {
                _resources.Context.CopyResource(staging, texture);
                _resources.Context.Map(
                    staging,
                    0,
                    MapMode.Read,
                    Vortice.Direct3D11.MapFlags.None,
                    out MappedSubresource mapped).CheckError();
                try
                {
                    var bitmap = new Bitmap((int)desc.Width, (int)desc.Height, PixelFormat.Format32bppRgb);
                    bool disposeBitmap = false;
                    BitmapData? data = null;
                    try
                    {
                        data = bitmap.LockBits(
                            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                            ImageLockMode.WriteOnly,
                            PixelFormat.Format32bppRgb);
                        nuint rowBytes = (nuint)(bitmap.Width * 4);
                        for (int y = 0; y < bitmap.Height; y++)
                        {
                            CopyMemory(
                                IntPtr.Add(data.Scan0, y * data.Stride),
                                IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch),
                                rowBytes);
                        }

                        return bitmap;
                    }
                    catch
                    {
                        disposeBitmap = true;
                        throw;
                    }
                    finally
                    {
                        if (data is not null)
                            bitmap.UnlockBits(data);
                        if (disposeBitmap)
                            bitmap.Dispose();
                    }
                }
                finally
                {
                    _resources.Context.Unmap(staging, 0);
                }
            }
        }

        private ID3D11Texture2D GetOrCreateStaging(Texture2DDescription desc)
        {
            if (_staging is not null)
            {
                Texture2DDescription existing = _staging.Description;
                if (existing.Width == desc.Width &&
                    existing.Height == desc.Height &&
                    existing.Format == desc.Format)
                {
                    return _staging;
                }

                _staging.Dispose();
                _staging = null;
            }

            _staging = _resources.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
            return _staging;
        }

        private static void CopyBitmapIntoTarget(
            Bitmap source,
            BitmapData target,
            Rectangle targetRegion,
            Rectangle screenBounds,
            Rectangle intersection)
        {
            BitmapData? sourceData = null;
            try
            {
                sourceData = source.LockBits(
                    new Rectangle(0, 0, source.Width, source.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppRgb);

                int sourceX = intersection.X - screenBounds.X;
                int sourceY = intersection.Y - screenBounds.Y;
                int targetX = intersection.X - targetRegion.X;
                int targetY = intersection.Y - targetRegion.Y;
                nuint rowBytes = (nuint)(intersection.Width * 4);

                for (int y = 0; y < intersection.Height; y++)
                {
                    IntPtr sourceRow = IntPtr.Add(
                        sourceData.Scan0,
                        (sourceY + y) * sourceData.Stride + sourceX * 4);
                    IntPtr targetRow = IntPtr.Add(
                        target.Scan0,
                        (targetY + y) * target.Stride + targetX * 4);
                    CopyMemory(targetRow, sourceRow, rowBytes);
                }
            }
            finally
            {
                if (sourceData is not null)
                    source.UnlockBits(sourceData);
            }
        }

        private static void TryConfigureSession(GraphicsCaptureSession session)
        {
            // IsCursorCaptureEnabled ships in the 19041 projection, so call it directly.
            try { session.IsCursorCaptureEnabled = false; }
            catch { /* optional polish; capture still works if Windows denies it */ }

            // IsBorderRequired is Windows 11+ (build 22000) and isn't in the 19041 SDK
            // projection this project targets, so it can't be called directly. Reflection
            // sets it on newer OSes and no-ops on older ones. ponytail: reflection stays
            // until the target platform is raised to 22000+.
            TrySetSessionProperty(session, "IsBorderRequired", false);
        }

        private static void TrySetSessionProperty(GraphicsCaptureSession session, string propertyName, bool value)
        {
            try
            {
                var property = session.GetType().GetProperty(propertyName);
                if (property?.CanWrite == true)
                    property.SetValue(session, value);
            }
            catch
            {
                // Optional polish only; capture still works if Windows denies it.
            }
        }

        public void Dispose()
        {
            lock (_frameGate)
            {
                _disposed = true;
                _latestBitmap?.Dispose();
                _latestBitmap = null;
                Monitor.PulseAll(_frameGate);
            }

            try
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }
            catch
            {
            }

            _session.Dispose();
            _framePool.Dispose();
            _staging?.Dispose();
            _staging = null;
        }
    }

    private sealed class DeviceResources : IDisposable
    {
        public DeviceResources()
        {
            D3D11CreateDevice(
                null!,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                null!,
                out ID3D11Device device,
                out FeatureLevel _,
                out ID3D11DeviceContext context).CheckError();

            Device = device;
            Context = context;
            using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
            WinRtDevice = CreateDirect3D11Device(dxgiDevice);
        }

        public ID3D11Device Device { get; }

        public ID3D11DeviceContext Context { get; }

        public IDirect3DDevice WinRtDevice { get; }

        public void Dispose()
        {
            Context.Dispose();
            Device.Dispose();
        }
    }

    private static GraphicsCaptureItem GetItemForScreen(WF.Screen screen)
    {
        string key = $"{screen.DeviceName}|{screen.Bounds.X},{screen.Bounds.Y},{screen.Bounds.Width},{screen.Bounds.Height}";
        if (ItemCache.TryGetValue(key, out GraphicsCaptureItem? existing))
            return existing;

        var created = CreateItemForMonitor(MonitorFromPoint(
            new POINT(screen.Bounds.Left + 1, screen.Bounds.Top + 1),
            MonitorDefaultToNearest));
        ItemCache[key] = created;
        return created;
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
            throw new Win32Exception("Monitor handle was not found.");

        IntPtr className = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        IntPtr itemPtr = IntPtr.Zero;

        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(
                "Windows.Graphics.Capture.GraphicsCaptureItem",
                44,
                out className));

            Guid factoryIid = IGraphicsCaptureItemInteropGuid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref factoryIid, out factory));

            IntPtr vtbl = Marshal.ReadIntPtr(factory);
            IntPtr methodPtr = Marshal.ReadIntPtr(vtbl, IntPtr.Size * 4);
            var createForMonitor = Marshal.GetDelegateForFunctionPointer<CreateForMonitorDelegate>(methodPtr);
            Guid itemIid = IGraphicsCaptureItemGuid;
            Marshal.ThrowExceptionForHR(createForMonitor(factory, monitor, ref itemIid, out itemPtr));

            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != IntPtr.Zero)
                Marshal.Release(itemPtr);
            if (factory != IntPtr.Zero)
                Marshal.Release(factory);
            if (className != IntPtr.Zero)
                WindowsDeleteString(className);
        }
    }

    private static IDirect3DDevice CreateDirect3D11Device(IDXGIDevice dxgiDevice)
    {
        IntPtr winrtDevicePtr = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDeviceNative(
                dxgiDevice.NativePointer,
                out winrtDevicePtr));
            return MarshalInterface<IDirect3DDevice>.FromAbi(winrtDevicePtr);
        }
        finally
        {
            if (winrtDevicePtr != IntPtr.Zero)
                Marshal.Release(winrtDevicePtr);
        }
    }

    private static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = IntPtr.Zero;
        IntPtr accessPtr = IntPtr.Zero;
        try
        {
            surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            Guid accessIid = IDirect3DDxgiInterfaceAccessGuid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, ref accessIid, out accessPtr));

            IntPtr vtbl = Marshal.ReadIntPtr(accessPtr);
            IntPtr methodPtr = Marshal.ReadIntPtr(vtbl, IntPtr.Size * 3);
            var getInterface = Marshal.GetDelegateForFunctionPointer<GetInterfaceDelegate>(methodPtr);
            Guid textureIid = typeof(ID3D11Texture2D).GUID;
            Marshal.ThrowExceptionForHR(getInterface(accessPtr, ref textureIid, out IntPtr texturePtr));
            return new ID3D11Texture2D(texturePtr);
        }
        finally
        {
            if (accessPtr != IntPtr.Zero)
                Marshal.Release(accessPtr);
            if (surfacePtr != IntPtr.Zero)
                Marshal.Release(surfacePtr);
        }
    }

    private static readonly Guid IGraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IGraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IDirect3DDxgiInterfaceAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForMonitorDelegate(IntPtr thisPtr, IntPtr monitor, ref Guid iid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceDelegate(IntPtr thisPtr, ref Guid iid, out IntPtr result);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDeviceNative(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct POINT(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }
}
