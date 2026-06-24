using System.Threading;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class HotkeyConflictTests
{
    [Theory]
    [InlineData("control + shift + s", "Ctrl+Shift+S")]
    [InlineData("win+shift+s", "Win+Shift+S")]
    [InlineData("Alt+Z", "Alt+Z")]
    public void TryNormalizeGesture_ReturnsStableDisplayText(string input, string expected)
    {
        Assert.True(HotkeyManager.TryNormalizeGesture(input, out string? normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void DescribeConflict_RecognizesKnownWindowsShortcut()
    {
        var source = HotkeyConflictInspector.DescribeConflict(
            "Win+Shift+S",
            Array.Empty<HotkeyConflictInspector.ProcessCandidate>());

        Assert.Equal("Windows Snipping Tool", source.DisplayName);
        Assert.True(source.IsExactMatch);
        Assert.Equal("snippingtool.exe", source.LaunchTarget);
    }

    [Fact]
    public void DescribeConflict_UsesLikelyRunningHotkeyAppWhenOwnerIsUnknown()
    {
        var source = HotkeyConflictInspector.DescribeConflict(
            "Ctrl+Shift+S",
            new[]
            {
                new HotkeyConflictInspector.ProcessCandidate("PowerToys", @"C:\Tools\PowerToys.exe"),
            });

        Assert.Equal("PowerToys", source.DisplayName);
        Assert.False(source.IsExactMatch);
        Assert.Equal(@"C:\Tools\PowerToys.exe", source.LaunchTarget);
    }

    [Fact]
    public void HotkeyOwnerProbe_EvaluateFindsForegroundProcessThatChangedAfterHotkey()
    {
        var baseline = new HotkeyOwnerProbe.Observation(
            10,
            "WinShot",
            @"C:\Tools\WinShot.exe",
            "WinShot Settings");
        var observations = new[]
        {
            baseline,
            new HotkeyOwnerProbe.Observation(
                25,
                "ShareX",
                @"C:\Tools\ShareX.exe",
                "ShareX"),
        };

        var result = HotkeyOwnerProbe.Evaluate("Ctrl+Shift+S", baseline, observations);

        Assert.True(result.Found);
        Assert.Equal("ShareX", result.Source.DisplayName);
        Assert.Equal(@"C:\Tools\ShareX.exe", result.Source.LaunchTarget);
    }

    [Fact]
    public void HotkeyOwnerProbe_EvaluateReturnsNoResultWhenForegroundNeverChanges()
    {
        var baseline = new HotkeyOwnerProbe.Observation(
            10,
            "WinShot",
            @"C:\Tools\WinShot.exe",
            "WinShot Settings");

        var result = HotkeyOwnerProbe.Evaluate("Ctrl+Shift+S", baseline, new[] { baseline });

        Assert.False(result.Found);
        Assert.Equal("another app on this PC", result.Source.DisplayName);
    }

    [Fact]
    public void HotkeyAssignmentValidator_AllowsUnchangedRegisteredHotkey() => RunOnSta(() =>
    {
        var box = new TextBox { Text = "Ctrl+Shift+1" };
        var fields = new[]
        {
            new HotkeyAssignmentValidator.Field("Capture region", box, "Ctrl+Shift+1"),
        };

        var result = HotkeyAssignmentValidator.Validate(fields, _ => HotkeyAvailabilityStatus.Unavailable);

        Assert.True(result.IsValid);
    });

    [Fact]
    public void HotkeyAssignmentValidator_BlocksDuplicateWinShotHotkey() => RunOnSta(() =>
    {
        var first = new TextBox { Text = "Ctrl+Shift+1" };
        var second = new TextBox { Text = "control+shift+1" };
        var fields = new[]
        {
            new HotkeyAssignmentValidator.Field("Capture region", first, "Ctrl+Shift+1"),
            new HotkeyAssignmentValidator.Field("Capture fullscreen", second, "Ctrl+Shift+2"),
        };

        var result = HotkeyAssignmentValidator.Validate(fields, _ => HotkeyAvailabilityStatus.Available);

        Assert.False(result.IsValid);
        Assert.Equal(HotkeyAssignmentIssueKind.DuplicateInWinShot, result.Issues.Single().Kind);
        Assert.Contains(first, result.Issues.Single().Boxes);
        Assert.Contains(second, result.Issues.Single().Boxes);
    });

    [Fact]
    public void HotkeyAssignmentValidator_BlocksChangedUnavailableHotkey() => RunOnSta(() =>
    {
        var box = new TextBox { Text = "Ctrl+Shift+S" };
        var fields = new[]
        {
            new HotkeyAssignmentValidator.Field("Capture region", box, "Ctrl+Shift+1"),
        };

        var result = HotkeyAssignmentValidator.Validate(fields, _ => HotkeyAvailabilityStatus.Unavailable);

        Assert.False(result.IsValid);
        Assert.Equal(HotkeyAssignmentIssueKind.UsedByAnotherApp, result.Issues.Single().Kind);
        Assert.Equal("Ctrl+Shift+S", result.Issues.Single().Gesture);
    });

    [Fact]
    public void HotkeyManager_IgnoresDispatchWhileHotkeyInputCaptureIsActive() => RunOnSta(() =>
    {
        using var manager = new HotkeyManager();
        int calls = 0;
        AddHotkeyHandlerForTest(manager, id: 1, () => calls++);
        using var capture = HotkeyInputCapture.Begin();

        DispatchHotkeyForTest(manager, id: 1);

        Assert.Equal(0, calls);
    });

    private static void AddHotkeyHandlerForTest(HotkeyManager manager, int id, Action handler)
    {
        var field = typeof(HotkeyManager).GetField("_handlers", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(HotkeyManager), "_handlers");
        var handlers = (Dictionary<int, Action>)field.GetValue(manager)!;
        handlers[id] = handler;
    }

    private static void DispatchHotkeyForTest(HotkeyManager manager, int id)
    {
        var method = typeof(HotkeyManager).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(HotkeyManager), "WndProc");
        object?[] args = [IntPtr.Zero, 0x0312, new IntPtr(id), IntPtr.Zero, false];
        method.Invoke(manager, args);
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
