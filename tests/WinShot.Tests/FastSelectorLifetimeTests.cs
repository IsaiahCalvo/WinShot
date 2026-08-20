using System.Threading;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

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
