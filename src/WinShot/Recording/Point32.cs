using System.Runtime.InteropServices;

namespace WinShot.Recording;

/// <summary>Win32 POINT (two 32-bit ints). Shared by the cursor-info and mouse-hook interop structs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Point32
{
    public int X;
    public int Y;
}
