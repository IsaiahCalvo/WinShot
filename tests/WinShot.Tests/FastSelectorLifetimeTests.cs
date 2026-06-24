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
    public void RegionSelector_ReturnDisposesFormInsteadOfKeepingHiddenWindow()
    {
        RunSta(() =>
        {
            var selector = new FastRegionSelectorDialog(
                Task.FromResult(new List<WindowInfo>()),
                settings: null);

            FastRegionSelectorDialog.Return(selector);

            Assert.True(selector.IsDisposed);
        });
    }

    [Fact]
    public void AllInOneSelector_ReturnDisposesFormAndToolbarInsteadOfKeepingHiddenWindows()
    {
        RunSta(() =>
        {
            var selector = new FastAllInOneSelectorDialog(
                Task.FromResult(new List<WindowInfo>()),
                settings: null);
            var toolbar = GetToolbar(selector);

            FastAllInOneSelectorDialog.Return(selector);

            Assert.True(selector.IsDisposed);
            Assert.True(toolbar.IsDisposed);
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
