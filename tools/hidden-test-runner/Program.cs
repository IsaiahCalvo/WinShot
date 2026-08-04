using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Runs a command on a hidden Windows desktop so its windows (test render harnesses,
// capture overlays) never flash on the user's visible desktop or taskbar. Child
// processes (dotnet test -> testhost) inherit the hidden desktop automatically.
//
//   HiddenDesktopRunner.exe <exe> [args...]
//
// Exit code is the child's exit code.
internal static class Program
{
    private const string DesktopName = "WinShotHiddenTests";
    private const uint GenericAll = 0x10000000;
    private const uint Infinite = 0xFFFFFFFF;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: HiddenDesktopRunner <exe> [args...]");
            return 2;
        }

        IntPtr desktop = CreateDesktopW(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop failed");

        var commandLine = new StringBuilder();
        foreach (string arg in args)
        {
            if (commandLine.Length > 0)
                commandLine.Append(' ');
            commandLine.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
        }

        var startup = new Startupinfo
        {
            cb = Marshal.SizeOf<Startupinfo>(),
            lpDesktop = DesktopName,
        };
        if (!CreateProcessW(null, commandLine.ToString(), IntPtr.Zero, IntPtr.Zero, false, 0,
                IntPtr.Zero, Environment.CurrentDirectory, ref startup, out ProcessInformation process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"CreateProcess failed for: {commandLine}");
        }

        WaitForSingleObject(process.hProcess, Infinite);
        GetExitCodeProcess(process.hProcess, out uint exitCode);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        CloseDesktop(desktop);
        return (int)exitCode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startupinfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string desktop, IntPtr device, IntPtr devmode,
        uint flags, uint desiredAccess, IntPtr securityAttributes);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref Startupinfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
