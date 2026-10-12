using Xunit;
using System.Runtime.CompilerServices;
using WinShot.Core;

// These tests share the process, not just the machine: Motion's timer-resolution
// refcount is one static counter, WPF allows one Application per process, and the
// render harnesses drive real windows. Running two classes at once means a suite
// that passes and then fails on identical code — Motion.Holders reads another
// class's in-flight tween, or a theme load lands mid-Application-construction.
// Xunit's default is a collection per class, in parallel; this turns that off.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestAnimationDefaults
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // CI Windows images disable desktop animations. Tests that exercise a tween
        // need an enabled baseline; tests for reduced motion override this themselves.
        // This changes only the test process, not Windows settings or the shipped app.
        Motion.AnimationsEnabled = () => true;
    }
}
