using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WinShot.Core;

namespace WinShot.SettingsUi;

/// <summary>
/// Watches for the app that swallows a hotkey. Built on ThemedMessageDialog so it matches
/// the rest of the shell; the one thing it adds to that card is a live status line between
/// the body copy and the buttons.
/// </summary>
internal static class HotkeyOwnerProbeDialog
{
    public static HotkeyOwnerProbe.Result Show(Window owner, string gesture)
    {
        var (dialog, body, footer) = ThemedMessageDialog.Build(
            owner,
            $"Press {gesture} once",
            "WinShot will watch which app comes forward. This may still trigger that app.");

        var status = new TextBlock
        {
            Text = "Watching for 8 seconds…",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };
        status.SetResourceReference(TextBlock.FontSizeProperty, "TextBody");
        status.SetResourceReference(TextBlock.ForegroundProperty, "InfoBrush");
        body.Children.Insert(body.Children.Count - 1, status);

        HotkeyOwnerProbe.Result? result = null;

        ThemedMessageDialog.AddButton(footer, "Close", primary: false, () => { }).IsCancel = true;

        Button open = ThemedMessageDialog.AddButton(footer, "Open app", primary: true, () =>
        {
            string? target = result?.Source.LaunchTarget;
            if (string.IsNullOrWhiteSpace(target))
                return;

            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                dialog.Close();
            }
            catch (Exception ex)
            {
                // The one action on this card that used to swallow both outcomes: nothing
                // happened on success (the dialog stayed up) and nothing on failure either.
                Log.Error($"Failed to open '{target}' after hotkey probe", ex);
                status.Text = FriendlyError.For(ex);
            }
        });
        open.IsEnabled = false;

        var cts = new CancellationTokenSource();
        dialog.Closed += (_, _) => cts.Cancel();
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                result = await HotkeyOwnerProbe.RunAsync(gesture, TimeSpan.FromSeconds(8), cts.Token);
                if (result.Found)
                {
                    status.Text = $"Looks like {result.Source.DisplayName} caught {gesture}.";
                    open.IsEnabled = !string.IsNullOrWhiteSpace(result.Source.LaunchTarget);
                    dialog.Activate();
                }
                else
                {
                    status.Text = "WinShot could not identify the app. It may be a silent hotkey, or the app did not come forward.";
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey owner probe failed", ex);
                status.Text = "WinShot could not run the hotkey watcher.";
            }
        };

        dialog.ShowDialog();

        return result ?? new HotkeyOwnerProbe.Result(
            false,
            new HotkeyConflictSource(
                "another app on this PC",
                "ms-settings:keyboard",
                false,
                "WinShot could not identify the app that received the hotkey."),
            "WinShot could not identify the app that received the hotkey.");
    }
}
