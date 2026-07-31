using System.Reflection;
using System.IO;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

public class QuickAccessOverlayInteractionTests
{
    [Fact]
    public void ActionsExposeNamesAndKeyboardPaths()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            bool pin = false;
            bool annotate = false;
            overlay.PinRequested += _ => pin = true;
            overlay.EditRequested += _ => annotate = true;

            Assert.Equal("Quick Access capture preview", overlay.AccessibleName);
            Assert.Equal(5, overlay.AccessibilityObject.GetChildCount());
            Assert.Equal(
                new[] { "Pin", "Close", "Annotate", "Save", "Copy" },
                Enumerable.Range(0, 5).Select(i => overlay.AccessibilityObject.GetChild(i)!.Name).ToArray());
            Assert.Equal("Close (Ctrl+W)", overlay.AccessibilityObject.GetChild(1)!.Help);
            Assert.Equal("Open Annotate tool (Ctrl+E)", overlay.AccessibilityObject.GetChild(2)!.Help);
            Assert.Equal("Save (Ctrl+S)", overlay.AccessibilityObject.GetChild(3)!.Help);

            InvokeKey(overlay, WF.Keys.Control | WF.Keys.E);
            Assert.True(annotate);

            InvokeKey(overlay, WF.Keys.Tab);
            InvokeKey(overlay, WF.Keys.Enter);
            Assert.True(pin);
        });
    }

    [Fact]
    public void RequestedCornerLayoutUsesSuppliedSaveSvgAndCentersCopy()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var buttons = GetButtons(overlay);

            var pin = Button(buttons, "Pin");
            var close = Button(buttons, "Close");
            var annotate = Button(buttons, "Annotate");
            var save = Button(buttons, "Save");
            var copy = Button(buttons, "Copy");

            Assert.Equal(pin.Bounds.Left, annotate.Bounds.Left);
            Assert.Equal(close.Bounds.Left, save.Bounds.Left);
            Assert.Equal(pin.Bounds.Top, close.Bounds.Top);
            Assert.Equal(annotate.Bounds.Top, save.Bounds.Top);
            Assert.True(save.IconLoaded);
            Assert.InRange(copy.Bounds.Left + copy.Bounds.Width / 2, overlay.ClientSize.Width / 2 - 1, overlay.ClientSize.Width / 2 + 1);
            Assert.InRange(copy.Bounds.Top + copy.Bounds.Height / 2, overlay.ClientSize.Height / 2 - 1, overlay.ClientSize.Height / 2 + 1);
        });
    }

    [Fact]
    public void OverflowKeepsOcrAndBackgroundWithoutCloud()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var field = typeof(FastQuickActionsWindow).GetField("_overflowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var menu = (WF.ContextMenuStrip)field.GetValue(overlay)!;

            Assert.Equal(new[] { "Recognize text (OCR)", "Add background" }, menu.Items.Cast<WF.ToolStripItem>().Select(i => i.Text));
            Assert.DoesNotContain(menu.Items.Cast<WF.ToolStripItem>(), i => i.Text?.Contains("Cloud", StringComparison.OrdinalIgnoreCase) == true);
        });
    }

    [Fact]
    public void HoverPausesAndResumesAutoCloseTimer()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            settings.Current.OverlayAutoClose = true;
            settings.Current.OverlayAutoCloseSeconds = 20;
            using var overlay = new FastQuickActionsWindow(bitmap, settings);
            var timerField = typeof(FastQuickActionsWindow).GetField("_autoCloseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timer = (WF.Timer)timerField.GetValue(overlay)!;

            Assert.True(timer.Enabled);
            Invoke(overlay, "SetHovering", true);
            Assert.False(timer.Enabled);
            Invoke(overlay, "SetHovering", false);
            Assert.True(timer.Enabled);
        });
    }

    [Fact]
    public void OnlyOneStackedCardKeepsItsHoverActionsExpanded()
    {
        RunSta(() =>
        {
            using var firstBitmap = new SD.Bitmap(1200, 760);
            using var secondBitmap = new SD.Bitmap(1200, 760);
            using var first = new FastQuickActionsWindow(firstBitmap, new SettingsService());
            using var second = new FastQuickActionsWindow(secondBitmap, new SettingsService());
            var hovering = typeof(FastQuickActionsWindow).GetField("_hovering", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Invoke(first, "SetHovering", true);
            Invoke(second, "SetHovering", true);

            Assert.False((bool)hovering.GetValue(first)!);
            Assert.True((bool)hovering.GetValue(second)!);
        });
    }

    [Fact]
    public void NativeWindowClassRequestsDropShadow()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var property = typeof(FastQuickActionsWindow).GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var createParams = (WF.CreateParams)property.GetValue(overlay)!;

            Assert.NotEqual(0, createParams.ClassStyle & 0x00020000);
        });
    }

    [Fact]
    public void ExportSaveBehaviorWritesDirectlyToConfiguredFolder()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winshot-qao-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            RunSta(() =>
            {
                using var bitmap = new SD.Bitmap(1200, 760);
                var settings = new SettingsService();
                settings.Current.SaveFolder = directory;
                settings.Current.ImageFormat = "png";
                settings.Current.FileNameTemplate = "Quick Access Test";
                settings.Current.OverlaySaveButtonBehavior = "export";
                using var overlay = new FastQuickActionsWindow(bitmap, settings);
                var method = typeof(FastQuickActionsWindow).GetMethod("SaveCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

                var task = (Task)method.Invoke(overlay, new object[] { false, false })!;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    WF.Application.DoEvents();
                    Thread.Sleep(10);
                }
                task.GetAwaiter().GetResult();

                Assert.Single(Directory.GetFiles(directory, "*.png"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void InvokeKey(FastQuickActionsWindow overlay, WF.Keys keys)
        => Invoke(overlay, "OnOverlayKeyDown", overlay, new WF.KeyEventArgs(keys));

    private static void Invoke(FastQuickActionsWindow overlay, string method, params object[] args)
        => typeof(FastQuickActionsWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overlay, args);

    private static List<object> GetButtons(FastQuickActionsWindow overlay)
    {
        var field = typeof(FastQuickActionsWindow).GetField("_buttons", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((System.Collections.IEnumerable)field.GetValue(overlay)!).Cast<object>().ToList();
    }

    private static ButtonSnapshot Button(IEnumerable<object> buttons, string name)
    {
        object button = buttons.Single(item =>
            string.Equals(
                item.GetType().GetProperty("AccessibleName")!.GetValue(item) as string,
                name,
                StringComparison.Ordinal));
        return new ButtonSnapshot(
            (SD.Rectangle)button.GetType().GetProperty("Bounds")!.GetValue(button)!,
            button.GetType().GetProperty("Icon")!.GetValue(button) is SD.Bitmap);
    }

    private readonly record struct ButtonSnapshot(SD.Rectangle Bounds, bool IconLoaded);

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
