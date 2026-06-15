using System.Windows;
using System.Windows.Input;

namespace WinShot.Scrolling;

/// <summary>
/// Small dark chooser shown before a scrolling capture: auto-scroll (WinShot
/// drives the wheel) or manual (the user scrolls themselves), a vertical or
/// horizontal direction, plus Cancel.
/// </summary>
public partial class ScrollingModeDialog : Window
{
    private ScrollingModeDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the chooser modally and returns the selected mode and direction,
    /// or null if the user cancelled (Cancel button or Esc). Must be called on
    /// the UI thread.
    /// </summary>
    public static ScrollCaptureChoice? Choose()
    {
        var dialog = new ScrollingModeDialog();
        if (dialog.ShowDialog() != true)
            return null;
        var mode = dialog.ManualRadio.IsChecked == true ? ScrollCaptureMode.Manual : ScrollCaptureMode.Auto;
        var direction = dialog.HorizontalRadio.IsChecked == true
            ? ScrollDirection.Horizontal
            : ScrollDirection.Vertical;
        return new ScrollCaptureChoice(mode, direction);
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
