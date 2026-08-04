using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Fails the suite when it runs on the user's visible desktop. Many tests open real
/// windows (render harnesses, capture overlays); on the interactive desktop those flash
/// across the screen and taskbar while someone is working. tools\run-tests.ps1 runs the
/// suite on a hidden desktop where nothing is visible.
/// </summary>
public class VisibleDesktopGuardTests
{
    [Fact]
    public void TestsMustNotRunOnTheVisibleDesktop()
    {
        // CI machines have no user watching; a headless service session is equally safe.
        if (Environment.GetEnvironmentVariable("CI") is not null ||
            Environment.GetEnvironmentVariable("WINSHOT_ALLOW_VISIBLE_TESTS") == "1" ||
            !Environment.UserInteractive)
            return;

        string desktop = CurrentDesktopName();
        Assert.False(string.Equals(desktop, "Default", StringComparison.OrdinalIgnoreCase),
            "Test windows would flash on the user's screen: the suite is running on the " +
            "visible desktop. Run it via tools\\run-tests.ps1 (hidden desktop), or set " +
            "WINSHOT_ALLOW_VISIBLE_TESTS=1 to override deliberately.");
    }

    private static string CurrentDesktopName()
    {
        IntPtr desktop = GetThreadDesktop(GetCurrentThreadId());
        var name = new StringBuilder(256);
        return GetUserObjectInformationW(desktop, 2 /* UOI_NAME */, name, (uint)name.Capacity * 2, out _)
            ? name.ToString()
            : "(unknown)";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr obj, int index,
        StringBuilder info, uint length, out uint lengthNeeded);
}
