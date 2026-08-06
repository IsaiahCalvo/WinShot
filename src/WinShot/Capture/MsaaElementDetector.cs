using System.Runtime.InteropServices;
using Accessibility;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Element rectangles for Chromium/Electron windows via MSAA (IAccessible) hit-test
/// descent. Managed UIA sees only Windows' lossy MSAA-to-UIA proxy on pre-Chromium-138
/// Electron (sparse trees, huge container boxes); the full DOM-level tree lives on the
/// MSAA side — what NVDA walks — and Chromium MATERIALIZES it when OBJID_CLIENT is
/// resolved on the Chrome_RenderWidgetHostHWND child. Blocking COM; call from a worker.
/// </summary>
internal static class MsaaElementDetector
{
    private const int WmGetObject = 0x003D;
    private const uint ObjIdClient = 0xFFFFFFFC;
    private static Guid _iidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private static Guid _iidIAccessible2 = new("E89F726E-C4F4-4c19-BB19-B647D7FA8478");

    // Electron ≥27 checks for this exact mutex name and keeps renderer accessibility
    // enabled process-wide while it exists. Created once, held for the app's lifetime.
    private static Mutex? _narratorMutex;

    public static void HoldScreenReaderMutex()
    {
        try
        {
            _narratorMutex ??= new Mutex(false, "NarratorRunning");
        }
        catch (Exception ex)
        {
            Log.Error("NarratorRunning mutex unavailable (non-fatal)", ex);
        }
    }

    /// <summary>Deepest accessible element whose bounds contain the point, by accHitTest
    /// descent from the render widget's client root. Null when the tree stays empty.</summary>
    public static SD.Rectangle? ElementRectFromPoint(IntPtr renderWidget, SD.Point screenPx, TimeSpan budget)
    {
        if (renderWidget == IntPtr.Zero)
            return null;
        try
        {
            // The screen-reader honeypot: answering lParam=1 flips Chromium's "assistive
            // tech is present" bit; resolving OBJID_CLIENT then builds the web tree.
            SendMessage(renderWidget, WmGetObject, IntPtr.Zero, new IntPtr(1));
            if (AccessibleObjectFromWindow(renderWidget, ObjIdClient, ref _iidIAccessible,
                    out IAccessible? root) != 0 || root is null)
                return null;

            EscalateAxMode(root);

            // Tree construction is asynchronous after the wake — poll briefly.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < budget)
            {
                try
                {
                    if (root.accChildCount > 0)
                        break;
                }
                catch (COMException) { }
                Thread.Sleep(50);
            }

            return Descend(root, screenPx);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SD.Rectangle? Descend(IAccessible root, SD.Point p)
    {
        IAccessible current = root;
        SD.Rectangle? best = null;
        for (int depth = 0; depth < 40; depth++)
        {
            object hit;
            try
            {
                hit = current.accHitTest(p.X, p.Y);
            }
            catch (Exception)
            {
                break; // cross-proc MSAA throws freely during tree churn
            }

            if (hit is IAccessible child && !ReferenceEquals(child, current))
            {
                if (Location(child, 0) is SD.Rectangle r && r.Contains(p))
                    best = r;
                current = child;
                continue;
            }
            if (hit is int childId)
            {
                if (childId != 0 && Location(current, childId) is SD.Rectangle r && r.Contains(p))
                    best = r;
                break; // simple child or self — cannot descend further
            }
            break;
        }
        // A whole-window rect adds nothing over the coarse fallback.
        if (best is SD.Rectangle rect && (rect.Width < 12 || rect.Height < 8))
            return null;
        return best;
    }

    private static SD.Rectangle? Location(IAccessible acc, object childId)
    {
        try
        {
            acc.accLocation(out int l, out int t, out int w, out int h, childId);
            return w > 0 && h > 0 ? new SD.Rectangle(l, t, w, h) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Asking the root for IAccessible2 signals an advanced client and bumps
    /// Chromium's accessibility mode toward extended properties. The answer is discarded —
    /// plain MSAA accLocation is all we need.</summary>
    private static void EscalateAxMode(IAccessible root)
    {
        try
        {
            if (root is IServiceProvider sp &&
                sp.QueryService(ref _iidIAccessible, ref _iidIAccessible2, out IntPtr ppv) == 0 &&
                ppv != IntPtr.Zero)
            {
                Marshal.Release(ppv);
            }
        }
        catch (Exception) { }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
