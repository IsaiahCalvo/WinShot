using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace WinShot.Core;

/// <summary>
/// Registers the bundled Manrope / JetBrains Mono TTFs (WPF pack resources) with
/// GDI via AddFontMemResourceEx so the WinForms Fast* surfaces can construct
/// System.Drawing.Font by family name ("Manrope", "Manrope SemiBold",
/// "JetBrains Mono", "JetBrains Mono SemiBold"). Process-private: nothing is
/// installed on the machine. WPF reads the same files directly through the
/// UiFontFamily/MonoFontFamily resources in Theme.xaml and never touches this.
/// </summary>
public static class AppFonts
{
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

    private static bool _loaded;

    /// <summary>True once the bundled families are visible to GDI; false means fall back to Segoe UI.</summary>
    public static bool Loaded => _loaded;

    public static void Register()
    {
        if (_loaded) return;
        var ok = true;
        foreach (var name in new[]
                 {
                     "Manrope-Regular.ttf", "Manrope-SemiBold.ttf", "Manrope-Bold.ttf",
                     "JetBrainsMono-Regular.ttf", "JetBrainsMono-SemiBold.ttf",
                 })
        {
            ok &= AddFromResource($"pack://application:,,,/Assets/Fonts/{name}");
        }
        _loaded = ok;
    }

    private static bool AddFromResource(string packUri)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri(packUri));
            if (info is null) return false;
            using var stream = info.Stream;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            var mem = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            uint count = 0;
            // GDI copies the data; the handle stays valid for the process lifetime.
            var handle = AddFontMemResourceEx(mem, (uint)bytes.Length, IntPtr.Zero, ref count);
            Marshal.FreeCoTaskMem(mem);
            return handle != IntPtr.Zero && count > 0;
        }
        catch
        {
            return false;
        }
    }
}
