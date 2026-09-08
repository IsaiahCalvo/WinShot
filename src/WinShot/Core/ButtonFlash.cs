using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WinShot.Core;

/// <summary>
/// "Copied" / a checkmark, held for a beat, then the button says what it does again.
///
/// The two copies of this that grew in the editor and the background composer each started
/// their own <see cref="DispatcherTimer"/> and never stopped it on the way out, so closing
/// the window inside the beat left a running timer holding the button — and the button
/// holding the window — until it ticked. Worse, a second click inside the beat started a
/// SECOND timer whose tick restored the glyph while the first flash was still meant to be
/// showing, so the confirmation blinked out early on exactly the impatient double-click
/// that most wants to see it.
///
/// One helper, keyed on the button: a second flash replaces the first rather than racing
/// it, and the beat is cut short when the button leaves the tree.
/// </summary>
public static class ButtonFlash
{
    /// <summary>How long a confirmation stays up. Long enough to read, short enough not to
    /// leave the button lying about what it does.</summary>
    public const int HoldMs = 1200;

    // ponytail: plain Dictionary, no lock. Every caller is on the one WPF UI thread, which
    // is what makes the DispatcherTimer legal in the first place. If a second UI thread ever
    // flashes a button, key this per-Dispatcher rather than adding a lock to the fast path.
    private static readonly Dictionary<ContentControl, Flash> Running = new();

    private sealed record Flash(DispatcherTimer Timer, RoutedEventHandler Unloaded);

    /// <summary>
    /// Shows <paramref name="flash"/> on the button, then puts <paramref name="normal"/>
    /// back. Safe to call again before the first flash ends, and safe to call on a button
    /// whose window is about to close.
    /// </summary>
    public static void Show(ContentControl button, object flash, object normal)
    {
        ArgumentNullException.ThrowIfNull(button);
        Stop(button);

        void Restore()
        {
            Stop(button);
            button.Content = normal;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Normal, button.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(HoldMs),
        };
        timer.Tick += (_, _) => Restore();

        // Unloaded fires when the window closes, which is the leak the old copies had: the
        // restore would otherwise run against a button nobody can see, keeping the window
        // alive for the rest of the beat.
        RoutedEventHandler unloaded = (_, _) => Restore();
        button.Unloaded += unloaded;

        button.Content = flash;
        Running[button] = new Flash(timer, unloaded);
        timer.Start();
    }

    /// <summary>Ends any flash on this button without restoring its content.</summary>
    private static void Stop(ContentControl button)
    {
        if (!Running.Remove(button, out Flash? flash)) return;
        flash.Timer.Stop();
        button.Unloaded -= flash.Unloaded;
    }

    /// <summary>Whether a flash is currently showing on this button. For tests.</summary>
    internal static bool IsFlashing(ContentControl button) => Running.ContainsKey(button);
}
