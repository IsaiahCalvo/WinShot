using System.Windows;
using System.Windows.Input;

namespace WinShot.Scrolling;

/// <summary>
/// Small dark chooser shown before a scrolling capture: auto-scroll (WinShot
/// drives the wheel) or manual (the user scrolls themselves), plus Cancel.
/// </summary>
public partial class ScrollingModeDialog : Window
{
    private ScrollingModeDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the chooser modally and returns the selected mode, or null if the
    /// user cancelled (Cancel button or Esc). Must be called on the UI thread.
    /// </summary>
    public static ScrollCaptureMode? Choose()
    {
        var dialog = new ScrollingModeDialog();
        if (dialog.ShowDialog() != true)
            return null;
        return dialog.ManualRadio.IsChecked == true ? ScrollCaptureMode.Manual : ScrollCaptureMode.Auto;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
        base.OnKeyDown(e);
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* button released mid-call */ }
        }
    }

    private void OnStart(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
