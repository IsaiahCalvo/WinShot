using System.Linq;
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
/// <summary>One level of the accessibility tree under the cursor: where it is and what the
/// app says it IS. The role is the whole point — it separates the label inside a button from
/// the button, and a page's banner or footer from the paragraph sitting in it.</summary>
public readonly record struct AxNode(SD.Rectangle Rect, int Role)
{
    // MSAA role constants (oleacc.h). Only the ones worth naming.
    public const int Titlebar = 1, Scrollbar = 3, Window = 9, Client = 10, MenuPopup = 11,
        Document = 15, Pane = 16, Dialog = 18, Grouping = 20, Separator = 21, Toolbar = 22,
        Table = 24, Cell = 29, Link = 30, List = 33, ListItem = 34, Outline = 35,
        OutlineItem = 36, PageTab = 37, StaticText = 41, Text = 42, PushButton = 43,
        CheckButton = 44, RadioButton = 45, ComboBox = 46, MenuItem = 12;

    /// <summary>Roles that name a thing a person would point at and call "that". These are
    /// what the wheel-free default should land on — the button, the row, the field, the tab.</summary>
    public bool IsPrimary => Role is PushButton or CheckButton or RadioButton or ComboBox
        or Link or ListItem or OutlineItem or Cell or PageTab or MenuItem or Text;

    /// <summary>Roles that bound a REGION rather than a control — a header, a footer, a
    /// banner, a toolbar, a scrollable pane. Worth a rung on the ladder, never the default.</summary>
    public bool IsRegion => Role is Grouping or Toolbar or List or Outline or Table
        or Document or Pane or Dialog or MenuPopup;

    /// <summary>Structural noise: the raw window/client wrappers and the text runs inside a
    /// control. Real rects, but never what someone means by "this element".</summary>
    public bool IsPassThrough => Role is Window or Client or StaticText or Separator
        or Scrollbar or Titlebar;
}

internal static class MsaaElementDetector
{
    private const int WmGetObject = 0x003D;
    private const uint ObjIdClient = 0xFFFFFFFC;
    private static Guid _iidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private static Guid _iidIAccessible2 = new("E89F726E-C4F4-4c19-BB19-B647D7FA8478");

    // Electron ≥27 checks for this exact mutex name and keeps renderer accessibility
    // enabled process-wide while it exists. Created once, held for the app's lifetime.
    private static Mutex? _narratorMutex;

    // Render widgets whose tree has already been seen populated. Building it is the slow
    // part — the first lookup on a window waits out a poll loop for it, every later one
    // would pay that wait again for nothing. An entry is dropped the moment a tree comes
    // back empty, so a recycled HWND or a torn-down renderer heals itself.
    private static readonly HashSet<IntPtr> AwakeTrees = new();

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

    /// <summary>
    /// The nesting under the cursor, innermost first: hit-test down to the deepest element,
    /// then walk accParent back up collecting every ancestor that still contains the point.
    ///
    /// The descent alone is not enough. Chromium's accHitTest jumps straight to the leaf, so
    /// asking only it hands back the text run inside a button and never the button, the row,
    /// or the region around them. The ancestors are where "this is a footer" lives.
    /// </summary>
    public static IReadOnlyList<AxNode> ElementChainFromPoint(IntPtr renderWidget, SD.Point screenPx, TimeSpan budget)
    {
        if (renderWidget == IntPtr.Zero)
            return Array.Empty<AxNode>();
        try
        {
            // The screen-reader honeypot: answering lParam=1 flips Chromium's "assistive
            // tech is present" bit; resolving OBJID_CLIENT then builds the web tree.
            SendMessage(renderWidget, WmGetObject, IntPtr.Zero, new IntPtr(1));
            if (AccessibleObjectFromWindow(renderWidget, ObjIdClient, ref _iidIAccessible,
                    out IAccessible? root) != 0 || root is null)
                return Array.Empty<AxNode>();

            EscalateAxMode(root);

            if (!IsTreeKnownAwake(renderWidget))
            {
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
            }

            SetTreeAwake(renderWidget, HasChildren(root));
            return Chain(root, screenPx);
        }
        catch (Exception)
        {
            return Array.Empty<AxNode>();
        }
    }

    /// <summary>Whether this render widget's accessibility tree has already been seen
    /// populated, so a later lookup can skip the poll loop entirely.</summary>
    public static bool IsTreeKnownAwake(IntPtr renderWidget)
    {
        lock (AwakeTrees)
            return AwakeTrees.Contains(renderWidget);
    }

    private static void SetTreeAwake(IntPtr renderWidget, bool awake)
    {
        lock (AwakeTrees)
        {
            if (awake)
                AwakeTrees.Add(renderWidget);
            else
                AwakeTrees.Remove(renderWidget);
        }
    }

    private static bool HasChildren(IAccessible root)
    {
        try
        {
            return root.accChildCount > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<AxNode> Chain(IAccessible root, SD.Point p)
    {
        var chain = new List<AxNode>();

        // Descend as far as hit-testing will go, recording each level it passes through.
        IAccessible deepest = root;
        for (int depth = 0; depth < 40; depth++)
        {
            object hit;
            try
            {
                hit = deepest.accHitTest(p.X, p.Y);
            }
            catch (Exception)
            {
                break; // cross-proc MSAA throws freely during tree churn
            }

            if (hit is IAccessible child && !ReferenceEquals(child, deepest))
            {
                Add(chain, child, 0, p);
                deepest = child;
                continue;
            }
            if (hit is int childId && childId != 0)
            {
                // A "simple" child has no interface of its own; record it and stop.
                Add(chain, deepest, childId, p);
            }
            break;
        }

        // Then climb, which is where the button, the row and the region actually live.
        IAccessible current = deepest;
        for (int depth = 0; depth < 40; depth++)
        {
            IAccessible? parent;
            try
            {
                parent = current.accParent as IAccessible;
            }
            catch (Exception)
            {
                break;
            }
            if (parent is null || ReferenceEquals(parent, current))
                break;

            if (!Add(chain, parent, 0, p))
                break; // left the point behind — everything above is bigger and less relevant
            current = parent;
        }

        chain.Sort((a, b) => ((long)a.Rect.Width * a.Rect.Height).CompareTo((long)b.Rect.Width * b.Rect.Height));
        return chain;
    }

    /// <summary>Appends a node when it has a usable rect containing the point and is not a
    /// near-duplicate of one already collected. Returns whether the point was inside it.</summary>
    private static bool Add(List<AxNode> chain, IAccessible acc, object childId, SD.Point p)
    {
        if (Location(acc, childId) is not SD.Rectangle rect || !rect.Contains(p))
            return false;
        if (rect.Width < 12 || rect.Height < 8)
            return true; // too small to select, but the ancestors above it are still worth having

        int role = RoleOf(acc, childId);
        if (chain.Any(n => n.Rect == rect))
            return true;

        chain.Add(new AxNode(rect, role));
        return true;
    }

    private static int RoleOf(IAccessible acc, object childId)
    {
        try
        {
            return acc.get_accRole(childId) is int role ? role : 0;
        }
        catch (Exception)
        {
            return 0;
        }
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
