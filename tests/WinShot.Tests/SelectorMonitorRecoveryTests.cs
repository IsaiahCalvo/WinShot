using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

public class SelectorMonitorRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedPanesReturnToTheirScreensAfterWindowsMovesThem(bool allInOne)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using WF.Form selector = allInOne
                    ? new FastAllInOneSelectorDialog(() => Task.FromResult(new List<WindowInfo>()), null)
                    : new FastRegionSelectorDialog(() => Task.FromResult(new List<WindowInfo>()), null);
                Type type = selector.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                // Include every real screen as a pane, even on a one-screen test machine.
                type.GetField("_monitorBounds", flags)!.SetValue(selector, new SD.Rectangle(0, 0, 32, 32));
                MethodInfo createPanes = type.GetMethod("CreatePanes", flags)!;
                createPanes.Invoke(selector, null);
                var panes = ((IEnumerable)type.GetField("_panes", flags)!.GetValue(selector)!).Cast<WF.Form>().ToArray();
                Assert.NotEmpty(panes);
                var expected = panes.Select(p => p.Bounds).ToArray();
                var handles = panes.Select(p => p.Handle).ToArray();
                var displaced = new SD.Rectangle(WF.Screen.PrimaryScreen!.Bounds.Location, new SD.Size(64, 48));
                foreach (var pane in panes)
                {
                    // Windows can relocate a parked HWND when a dock/screen disconnects.
                    Assert.True(SetWindowPos(pane.Handle, IntPtr.Zero, displaced.X, displaced.Y,
                        displaced.Width, displaced.Height, 0x0004 | 0x0010));
                    Assert.Equal(displaced, pane.Bounds);
                }

                createPanes.Invoke(selector, null);

                for (int i = 0; i < panes.Length; i++)
                {
                    Assert.False(panes[i].IsDisposed);
                    Assert.Equal(handles[i], panes[i].Handle);
                    Assert.Equal(expected[i], panes[i].Bounds);
                    Assert.True(GetWindowRect(panes[i].Handle, out Rect actual));
                    Assert.Equal(expected[i], SD.Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom));
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
}
