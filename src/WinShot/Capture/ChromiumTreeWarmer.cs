using System.Runtime.InteropServices;
using System.Text;
using Accessibility;
using WinShot.Core;

namespace WinShot.Capture;

/// <summary>
/// Keeps Chromium/Electron accessibility trees BUILT before anyone hovers.
///
/// Chromium constructs its accessibility tree lazily — only when it believes assistive
/// technology is present — over one to three seconds, and tears it down when the client goes
/// away. Waking it at hover time therefore races a tree that doesn't exist yet, which is why
/// element detection in Electron apps answered with the whole window: the 450 ms hover budget
/// lost to a 1-3 s build. Screen readers never have this problem because they are connected
/// continuously; the tree is warm before the question is asked.
///
/// This does the same from a background timer: every sweep, find visible Chromium-family
/// windows, send the screen-reader honeypot (WM_GETOBJECT with lParam 1), resolve
/// OBJID_CLIENT, and HOLD the root IAccessible reference. The held reference is what marks us
/// as a live accessibility client so Chromium keeps the tree alive between sweeps.
///
/// ponytail: a 15 s timer over EnumWindows — no window-event hooks, no cleverness. The cost is
/// a held COM proxy per Chromium window and one cheap sweep; roots of closed windows are
/// dropped on the next sweep.
/// </summary>
internal static class ChromiumTreeWarmer
{
    private const int WmGetObject = 0x003D;
    private const uint ObjIdClient = 0xFFFFFFFC;
    private const int SweepMs = 15_000;
    private static Guid _iidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, IAccessible> Roots = new();
    private static Timer? _timer;

    public static void Start()
    {
        if (_timer is not null)
            return;
        // First sweep almost immediately so trees are warm shortly after login/startup.
        _timer = new Timer(_ => Sweep(), null, dueTime: 2_000, period: SweepMs);
    }

    private static void Sweep()
    {
        try
        {
            var widgets = new List<IntPtr>();
            EnumWindows((top, _) =>
            {
                if (!IsWindowVisible(top))
                    return true;
                if (IsChromiumTop(top))
                {
                    // The web content answers on the render widget when there is one, and on
                    // the top-level window in shells that expose no widget HWND at all.
                    IntPtr widget = FindRenderWidget(top);
                    widgets.Add(widget != IntPtr.Zero ? widget : top);
                }
                return true;
            }, IntPtr.Zero);

            lock (Gate)
            {
                // Drop roots whose windows are gone so closed apps can release their trees.
                foreach (IntPtr stale in Roots.Keys.Where(h => !IsWindow(h)).ToList())
                {
                    ReleaseRoot(stale);
                }

                foreach (IntPtr hwnd in widgets)
                {
                    if (Roots.ContainsKey(hwnd))
                    {
                        Touch(Roots[hwnd]); // keep-alive: an occasional query marks the client active
                        continue;
                    }

                    // The honeypot flips Chromium's "assistive tech present" bit; resolving
                    // OBJID_CLIENT starts the tree build. Async — by the time someone hovers,
                    // it is done.
                    SendMessageTimeout(hwnd, WmGetObject, IntPtr.Zero, new IntPtr(1),
                        0x0002 /* SMTO_ABORTIFHUNG */, 200, out _);
                    if (AccessibleObjectFromWindow(hwnd, ObjIdClient, ref _iidIAccessible,
                            out IAccessible? root) == 0 && root is not null)
                    {
                        Touch(root);
                        Roots[hwnd] = root;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Chromium tree warm sweep failed (non-fatal)", ex);
        }
    }

    private static void Touch(IAccessible root)
    {
        try
        {
            _ = root.accChildCount;
        }
        catch (Exception)
        {
            // Tree mid-rebuild — the reference itself is the signal that matters.
        }
    }

    private static void ReleaseRoot(IntPtr hwnd)
    {
        try
        {
            Marshal.ReleaseComObject(Roots[hwnd]);
        }
        catch (Exception)
        {
        }
        Roots.Remove(hwnd);
    }

    private static bool IsChromiumTop(IntPtr top)
    {
        var sb = new StringBuilder(64);
        GetClassName(top, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal))
            return true;
        // WebView2 hosts (Outlook, Raycast) bury Chromium under other classes.
        return FindRenderWidget(top) != IntPtr.Zero;
    }

    private static IntPtr FindRenderWidget(IntPtr top)
    {
        IntPtr found = IntPtr.Zero;
        var sb = new StringBuilder(64);
        EnumChildWindows(top, (child, _) =>
        {
            sb.Clear();
            GetClassName(child, sb, sb.Capacity);
            if (sb.ToString().Equals("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal))
            {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
}
