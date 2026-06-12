using System.Windows;
using System.Windows.Input;

namespace WinShot.Recording;

/// <summary>Small dark chooser shown before a recording: MP4 or GIF, plus microphone toggle for MP4.</summary>
public partial class RecordingOptionsDialog : Window
{
    public RecordingOptionsDialog(bool defaultRecordAudio)
    {
        InitializeComponent();
        AudioCheck.IsChecked = defaultRecordAudio;
    }

    public bool IsGif => GifRadio.IsChecked == true;

    public bool RecordAudio => Mp4Radio.IsChecked == true && AudioCheck.IsChecked == true;

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
