using System.IO;
using System.Windows;
using WinShot.Core;

namespace WinShot.Recording;

public partial class RecordingRecoveryWindow : Window
{
    internal RecordingRecoveryWindow(IReadOnlyList<RecoverableRecordingTempFile> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        InitializeComponent();

        SummaryText.Text = candidates.Count == 1
            ? "WinShot found one recording left by an earlier session."
            : $"WinShot found {candidates.Count} recordings left by an earlier session.";
        RecordingList.ItemsSource = candidates.Select(candidate => new RecordingRecoveryListItem(candidate));
        if (candidates.Count > 0)
            RecordingList.SelectedIndex = 0;

        DarkTitleBar.Apply(this);
    }

    private void OnRecover(object sender, RoutedEventArgs e) => DialogResult = true;

    private sealed record RecordingRecoveryListItem(string FileName, string Details)
    {
        internal RecordingRecoveryListItem(RecoverableRecordingTempFile candidate)
            : this(
                Path.GetFileName(candidate.Path),
                $"{FormatSize(candidate.Length)}  •  {candidate.LastWriteTimeUtc.ToLocalTime():g}  •  {candidate.Path}")
        {
        }

        private static string FormatSize(long bytes)
        {
            const double megabyte = 1024d * 1024d;
            return bytes >= megabyte
                ? $"{bytes / megabyte:0.#} MB"
                : $"{Math.Max(1, bytes / 1024d):0.#} KB";
        }
    }
}
