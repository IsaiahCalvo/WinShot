using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WinShot.Core;

namespace WinShot.Recording;

/// <summary>
/// Dark completion toast shown bottom-right after a recording is saved:
/// filename plus Open / Reveal / Edit… buttons. Auto-dismisses after 8 s
/// (paused while the pointer is over it). Pass a null <c>onEdit</c> to hide
/// the Edit… button (GIFs get Open/Reveal only).
/// </summary>
public partial class RecordingToastWindow : Window
{
    private readonly string _filePath;
    private readonly Action? _onEdit;
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    public RecordingToastWindow(string filePath, Action? onEdit)
    {
        InitializeComponent();
        _filePath = filePath;
        _onEdit = onEdit;
        FileNameText.Text = Path.GetFileName(filePath);
        FileNameText.ToolTip = filePath;
        if (onEdit is null)
            BtnEdit.Visibility = Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            PositionBottomRight();
            _dismissTimer.Tick += (_, _) => Close();
            _dismissTimer.Start();
        };
        Closed += (_, _) => _dismissTimer.Stop();
    }

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => _dismissTimer.Stop();

    private void OnMouseLeave(object sender, MouseEventArgs e) => _dismissTimer.Start();

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open recording {_filePath}", ex);
        }
        Close();
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_filePath}\"");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to reveal recording {_filePath}", ex);
        }
        Close();
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        try
        {
            _onEdit?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open video editor for {_filePath}", ex);
        }
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
