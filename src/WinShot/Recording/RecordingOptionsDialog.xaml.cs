using System.Windows;
using System.Windows.Input;
using WinShot.Core;

namespace WinShot.Recording;

/// <summary>
/// Dark chooser shown before a recording: MP4 or GIF, microphone/system audio,
/// webcam overlay corner, cursor capture, click highlights, keystroke overlay,
/// and a pre-roll countdown. Defaults come from settings; the caller persists
/// the user's choices back as the new defaults.
/// </summary>
public partial class RecordingOptionsDialog : Window
{
    public static void Prewarm(Settings settings)
    {
        var dialog = new RecordingOptionsDialog(settings);
        dialog.Close();
    }

    public RecordingOptionsDialog(Settings settings)
    {
        InitializeComponent();
        AudioCheck.IsChecked = settings.RecordAudio;
        SystemAudioCheck.IsChecked = settings.RecordSystemAudio;
        CursorCheck.IsChecked = settings.CaptureCursor;
        ClickHighlightCheck.IsChecked = settings.ShowClickHighlights;
        KeystrokeCheck.IsChecked = settings.ShowKeystrokes;
        CountdownBox.Text = RecordingOptions.ClampCountdownSeconds(settings.RecordingCountdownSeconds).ToString();
        WebcamSizeBox.Text = RecordingOptions.ClampWebcamSizePercent(settings.WebcamOverlaySizePercent).ToString();
        WebcamCombo.SelectedIndex = RecordingOptions.NormalizeWebcamPosition(settings.WebcamOverlayPosition) switch
        {
            "top-left" => 1,
            "top-right" => 2,
            "bottom-left" => 3,
            "bottom-right" => 4,
            "fullscreen" => 5,
            _ => 0,
        };
    }

    public bool IsGif => GifRadio.IsChecked == true;

    /// <summary>Raw checkbox state; only applies to MP4 (the control is disabled for GIF).</summary>
    public bool RecordMicrophone => AudioCheck.IsChecked == true;

    /// <summary>Raw checkbox state; only applies to MP4 (the control is disabled for GIF).</summary>
    public bool RecordSystemAudio => SystemAudioCheck.IsChecked == true;

    public bool CaptureCursor => CursorCheck.IsChecked == true;

    public bool ShowClickHighlights => ClickHighlightCheck.IsChecked == true;

    public bool ShowKeystrokes => KeystrokeCheck.IsChecked == true;

    /// <summary>Pre-roll countdown in seconds; 0 = off.</summary>
    public int CountdownSeconds =>
        int.TryParse(CountdownBox.Text.Trim(), out int s)
            ? RecordingOptions.ClampCountdownSeconds(s)
            : RecordingOptions.MinCountdownSeconds;

    /// <summary>"off" or one of "top-left" / "top-right" / "bottom-left" / "bottom-right".</summary>
    public string WebcamPosition => WebcamCombo.SelectedIndex switch
    {
        1 => "top-left",
        2 => "top-right",
        3 => "bottom-left",
        4 => "bottom-right",
        5 => "fullscreen",
        _ => "off",
    };

    public int WebcamSizePercent =>
        int.TryParse(WebcamSizeBox.Text.Trim(), out int percent)
            ? RecordingOptions.ClampWebcamSizePercent(percent)
            : RecordingOptions.DefaultWebcamSizePercent;

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
