using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Small dark modal that lists every monitor ("Display 1 (primary) — 2560x1440")
/// and returns the picked one's bounds in physical screen pixels.
/// </summary>
public partial class DisplayPickerDialog : Window
{
    private SD.Rectangle? _selected;

    private DisplayPickerDialog()
    {
        InitializeComponent();
        var screens = WF.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var button = new Button
            {
                Style = (Style)FindResource("DisplayButton"),
                Content = $"Display {i + 1}{(screen.Primary ? " (primary)" : "")} — {screen.Bounds.Width}x{screen.Bounds.Height}",
            };
            button.Click += (_, _) =>
            {
                _selected = screen.Bounds;
                DialogResult = true;
            };
            DisplayList.Children.Add(button);
        }
    }

    /// <summary>
    /// Lets the user pick a monitor; returns its bounds in physical screen
    /// pixels, or null on cancel. With a single monitor the dialog is skipped
    /// and its bounds are returned directly.
    /// </summary>
    public static SD.Rectangle? ChooseDisplay()
    {
        var screens = WF.Screen.AllScreens;
        if (screens.Length == 1)
            return screens[0].Bounds;

        var dialog = new DisplayPickerDialog();
        return dialog.ShowDialog() == true ? dialog._selected : null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
        base.OnKeyDown(e);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* button released mid-call */ }
        }
    }
}
