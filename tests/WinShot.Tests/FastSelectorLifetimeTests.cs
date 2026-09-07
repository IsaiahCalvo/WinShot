using System.Threading;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

// Constructs the selectors, which read the same static CursorProvider the Pointer helpers
// in SelectorInteractionTests set and clear. xunit runs collections in parallel, so a class
// touching a selector outside this collection is a cross-collection flake waiting to happen.
[Collection("MotionRefcount")]
public class FastSelectorLifetimeTests
{
    [Fact]
    public void RegionSelector_DoesNotRequestWindowListUntilShown()
    {
        RunSta(() =>
        {
            int requests = 0;
            using var selector = new FastRegionSelectorDialog(
                () =>
                {
                    requests++;
                    return Task.FromResult(new List<WindowInfo>());
                },
                settings: null);

            Assert.Equal(0, requests);
        });
    }

    [Fact]
    public void AllInOneSelector_DoesNotRequestWindowListUntilShown()
    {
        RunSta(() =>
        {
            int requests = 0;
            using var selector = new FastAllInOneSelectorDialog(
                () =>
                {
                    requests++;
                    return Task.FromResult(new List<WindowInfo>());
                },
                settings: null);

            Assert.Equal(0, requests);
        });
    }

    [Fact]
    public void RegionSelector_ReturnPoolsFormAndRentReusesIt()
    {
        RunSta(() =>
        {
            var selector = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()),
                settings: null);

            // Return keeps the instance warm (hidden, no bitmaps) so the next hotkey
            // press re-shows existing window handles instead of creating new ones.
            FastRegionSelectorDialog.Return(selector);
            Assert.False(selector.IsDisposed);

            var reused = FastRegionSelectorDialog.Rent(
                () => Task.FromResult(new List<WindowInfo>()),
                settings: null);
            Assert.Same(selector, reused);

            // Leave no cross-test pool state behind.
            reused.Dispose();
        });
    }

    [Fact]
    public void AllInOneSelector_ReturnPoolsFormAndRentReusesIt()
    {
        RunSta(() =>
        {
            var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()),
                settings: null);
            var toolbar = GetToolbar(selector);

            FastAllInOneSelectorDialog.Return(selector);
            Assert.False(selector.IsDisposed);
            Assert.False(toolbar.IsDisposed);

            var reused = FastAllInOneSelectorDialog.Rent(
                () => Task.FromResult(new List<WindowInfo>()),
                settings: null);
            Assert.Same(selector, reused);

            // Leave no cross-test pool state behind.
            reused.Dispose();
        });
    }

    private static WF.Form GetToolbar(FastAllInOneSelectorDialog selector)
    {
        var field = typeof(FastAllInOneSelectorDialog).GetField(
            "_toolbar",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (WF.Form)field.GetValue(selector)!;
    }

    /// <summary>
    /// The all-in-one selector used to re-crop the snapshot and alpha-blend a full-monitor
    /// dim on every paint, which is the 4K drag stutter the region selector already fixed.
    /// The baked slice must be built once per monitor and freed with the snapshot.
    /// </summary>
    [Fact]
    public void AllInOneSelector_BakesTheDimmedSliceOncePerMonitorAndFreesItWithTheSnapshot()
    {
        RunSta(() =>
        {
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()),
                settings: null);

            var monitor = new System.Drawing.Rectangle(0, 0, 64, 48);
            SetField(selector, "_vs", monitor);
            Assert.False(HasFullSurfaceBackground(selector));

            using var frozen = new System.Drawing.Bitmap(monitor.Width, monitor.Height);
            SetField(selector, "_frozen", frozen);
            Assert.True(HasFullSurfaceBackground(selector));

            object first = DimmedBackground(selector, monitor)!;
            object second = DimmedBackground(selector, monitor)!;
            Assert.Same(first, second);
            Assert.Single(DimmedCache(selector));

            // DisposeFrozen owns the cache too — otherwise every capture leaks a
            // full-monitor bitmap per display for the life of the pooled selector.
            typeof(FastAllInOneSelectorDialog)
                .GetMethod("DisposeFrozen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(selector, null);
            Assert.Empty(DimmedCache(selector));
            Assert.False(HasFullSurfaceBackground(selector));
            SetField(selector, "_frozen", null);
        });
    }

    private static void SetField(object target, string name, object? value) =>
        target.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static bool HasFullSurfaceBackground(FastAllInOneSelectorDialog selector) =>
        (bool)typeof(FastAllInOneSelectorDialog)
            .GetProperty("HasFullSurfaceBackground", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(selector)!;

    private static object? DimmedBackground(FastAllInOneSelectorDialog selector, System.Drawing.Rectangle monitor) =>
        typeof(FastAllInOneSelectorDialog)
            .GetMethod("GetDimmedBackground", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(selector, new object[] { monitor });

    private static System.Collections.IDictionary DimmedCache(FastAllInOneSelectorDialog selector) =>
        (System.Collections.IDictionary)typeof(FastAllInOneSelectorDialog)
            .GetField("_dimmedCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(selector)!;

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
