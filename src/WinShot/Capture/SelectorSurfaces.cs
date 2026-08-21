using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Parking for the selector's full-screen overlay surfaces.
///
/// Measured: making one full-screen layered window visible costs ~130 ms of DWM composition,
/// every time, even with a warm handle — three monitors meant ~370 ms of the hotkey-to-visible
/// delay was nothing but ShowWindow. Batching the shows through DeferWindowPos does not help
/// (the cost is per-surface in DWM, not in the window-manager transaction), so the surfaces are
/// never hidden between captures: they stay shown at alpha 0 and click-through, and opening the
/// selector is then just an alpha flip.
///
/// A parked surface is invisible and cannot be clicked, alt-tabbed, or captured (selector
/// surfaces already carry capture exclusion, and the BitBlt tier omits CAPTUREBLT so layered
/// windows are skipped there by construction). The cost is that DWM composites a few fully
/// transparent surfaces while the app idles.
/// </summary>
internal static class SelectorSurfaces
{
    /// <summary>Hides a shown surface without giving up its DWM composition, so re-showing is free.</summary>
    public static void Park(WF.Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        ScrollCaptureChromeAlpha(form.Handle, 0);
        SetClickThrough(form.Handle, true);
    }

    /// <summary>Brings a parked surface back to <paramref name="alpha"/> and makes it clickable again.</summary>
    public static void Unpark(WF.Form form, byte alpha)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        SetClickThrough(form.Handle, false);
        ScrollCaptureChromeAlpha(form.Handle, alpha);
    }

    private static void ScrollCaptureChromeAlpha(IntPtr handle, byte alpha)
        => WinShot.Scrolling.CaptureExclusion.SetLayeredAlpha(handle, alpha);

    private static void SetClickThrough(IntPtr handle, bool clickThrough)
    {
        nint style = GetWindowLongPtr(handle, GwlExStyle);
        nint updated = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        if (updated != style)
            SetWindowLongPtr(handle, GwlExStyle, updated);
    }

    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr handle, int index, nint value);
}
