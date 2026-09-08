// Timing-only benchmark for the selector's freeze-frame source.
//
// PRIVACY: this tool captures the desktop into memory purely to time the call and
// then disposes it immediately. It never writes, displays, hashes or transmits a
// single pixel — the only output is milliseconds and the frame size. That is the
// whole point: real numbers on real hardware without a screenshot existing anywhere.
//
//   dotnet run --project tools\bench-capture -c Release
//
// It answers one question: can the selector grab its freeze frame BEFORE the
// overlay is shown, i.e. is a warm source under ~50 ms on this desktop?
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using WF = System.Windows.Forms;

const int Runs = 8;

Rectangle vs = WF.SystemInformation.VirtualScreen;
Console.WriteLine($"virtual desktop {vs.Width}x{vs.Height} across {WF.Screen.AllScreens.Length} displays");

Report("gdi bitblt (SRCCOPY, no CAPTUREBLT)", () => BitBltCapture(vs));

using (var dup = new Duplication(vs))
{
    if (dup.Ready)
    {
        Console.WriteLine($"dxgi duplication cold setup (process-first): {dup.SetupMs} ms");
        Report("dxgi duplication (warm device)", () => dup.Capture());
    }
    else
    {
        Console.WriteLine($"dxgi duplication unavailable: {dup.Error}");
        return;
    }
}

// Is the 330 ms a one-time D3D runtime + driver DLL load for the PROCESS, or is it
// paid on every device creation? If it is one-time, a startup prewarm that creates
// and throws away a device buys every later cold capture the fast path, and the
// 30 s idle evictor can stay exactly as it is.
for (int i = 0; i < 3; i++)
{
    using var again = new Duplication(vs);
    Console.WriteLine($"dxgi duplication re-setup #{i + 1} (device disposed in between): {again.SetupMs} ms");
}

static void Report(string name, Func<Bitmap> capture)
{
    var samples = new List<long>(Runs);
    for (int i = 0; i < Runs; i++)
    {
        var sw = Stopwatch.StartNew();
        Bitmap bmp;
        try { bmp = capture(); }
        catch (Exception ex) { Console.WriteLine($"{name}: FAILED {ex.GetType().Name} {ex.Message}"); return; }
        sw.Stop();
        bmp.Dispose(); // pixels never leave this scope
        samples.Add(sw.ElapsedMilliseconds);
    }
    samples.Sort();
    Console.WriteLine(
        $"{name}: min={samples[0]} median={samples[samples.Count / 2]} max={samples[^1]} ms " +
        $"[{string.Join(" ", samples)}]");
}

static Bitmap BitBltCapture(Rectangle r)
{
    var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
    using var g = Graphics.FromImage(bmp);
    IntPtr dest = g.GetHdc();
    IntPtr src = GetDC(IntPtr.Zero);
    try
    {
        if (!BitBlt(dest, 0, 0, r.Width, r.Height, src, r.X, r.Y, 0x00CC0020))
            throw new InvalidOperationException($"BitBlt failed ({Marshal.GetLastWin32Error()})");
    }
    finally
    {
        if (src != IntPtr.Zero) ReleaseDC(IntPtr.Zero, src);
        g.ReleaseHdc(dest);
    }
    return bmp;
}

[DllImport("gdi32.dll", SetLastError = true)]
static extern bool BitBlt(IntPtr h, int x, int y, int w, int hgt, IntPtr src, int sx, int sy, int rop);
[DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
[DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
[DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
static extern void CopyMemory(IntPtr dst, IntPtr src, nuint len);

/// <summary>Mirrors WinShot's DesktopDuplicationCapture closely enough to time it.</summary>
sealed class Duplication : IDisposable
{
    private readonly Rectangle _virtual;
    private readonly List<Display> _displays = new();

    public Duplication(Rectangle virtualScreen)
    {
        _virtual = virtualScreen;
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var screen in WF.Screen.AllScreens)
                _displays.Add(new Display(screen));
            // First AcquireNextFrame is the expensive one; pay it in setup like a
            // prewarmed app would, so the reported per-capture number is the warm one.
            Capture().Dispose();
            Ready = true;
        }
        catch (Exception ex)
        {
            Error = $"{ex.GetType().Name}: {ex.Message}";
        }
        SetupMs = sw.ElapsedMilliseconds;
    }

    public bool Ready { get; }
    public string Error { get; } = "";
    public long SetupMs { get; }

    public Bitmap Capture()
    {
        var result = new Bitmap(_virtual.Width, _virtual.Height, PixelFormat.Format32bppRgb);
        BitmapData? data = null;
        try
        {
            data = result.LockBits(new Rectangle(0, 0, result.Width, result.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            foreach (var d in _displays)
                d.CopyInto(data, _virtual);
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { if (data is not null) result.UnlockBits(data); }
    }

    public void Dispose() { foreach (var d in _displays) d.Dispose(); }

    private sealed class Display : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _dup;
        private readonly ID3D11Texture2D _staging;
        private readonly Rectangle _bounds;
        private bool _hasFrame;

        public Display(WF.Screen screen)
        {
            _bounds = screen.Bounds;
            D3D11CreateDevice(null!, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null!,
                out _device, out FeatureLevel _, out _context).CheckError();
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            using var output = FindOutput(adapter, screen.DeviceName);
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _dup = output1.DuplicateOutput(_device);
            var desc = _dup.Description.ModeDescription;
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1,
                Format = desc.Format, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None, MiscFlags = ResourceOptionFlags.None,
            });
        }

        public void CopyInto(BitmapData target, Rectangle virtualScreen)
        {
            IDXGIResource? res = null;
            bool acquired = false;
            try
            {
                var r = _dup.AcquireNextFrame(_hasFrame ? 0u : 200u, out _, out res);
                if (r.Success)
                {
                    acquired = true;
                    using var tex = res!.QueryInterface<ID3D11Texture2D>();
                    _context.CopyResource(_staging, tex);
                    _hasFrame = true;
                }
                else if (!_hasFrame)
                {
                    r.CheckError();
                }
            }
            finally
            {
                res?.Dispose();
                if (acquired) _dup.ReleaseFrame();
            }

            _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None,
                out MappedSubresource m).CheckError();
            try
            {
                int tx = _bounds.X - virtualScreen.X;
                int ty = _bounds.Y - virtualScreen.Y;
                nuint row = (nuint)(_bounds.Width * 4);
                for (int y = 0; y < _bounds.Height; y++)
                {
                    CopyMemory(
                        IntPtr.Add(target.Scan0, (ty + y) * target.Stride + tx * 4),
                        IntPtr.Add(m.DataPointer, y * (int)m.RowPitch),
                        row);
                }
            }
            finally { _context.Unmap(_staging, 0); }
        }

        public void Dispose() { _staging.Dispose(); _dup.Dispose(); _context.Dispose(); _device.Dispose(); }

        private static IDXGIOutput FindOutput(IDXGIAdapter adapter, string name)
        {
            for (uint i = 0; ; i++)
            {
                adapter.EnumOutputs(i, out IDXGIOutput o);
                if (o is null) throw new InvalidOperationException($"output not found: {name}");
                if (string.Equals(o.Description.DeviceName, name, StringComparison.OrdinalIgnoreCase)) return o;
                o.Dispose();
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr dst, IntPtr src, nuint len);
    }
}
