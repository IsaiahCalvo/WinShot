using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinShot.Core;

namespace WinShot.SettingsUi;

internal sealed class HotkeyOwnerProbeDialog : Window
{
    private readonly string _gesture;
    private readonly TextBlock _statusText;
    private readonly Button _openButton;
    private readonly CancellationTokenSource _cts = new();
    private HotkeyOwnerProbe.Result? _result;

    private HotkeyOwnerProbeDialog(string gesture)
    {
        _gesture = gesture;
        Title = "Find app using hotkey";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        Foreground = Brushes.White;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(18) };
        Content = panel;

        panel.Children.Add(new TextBlock
        {
            Text = $"Press {_gesture} once",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "WinShot will watch which app comes forward. This may still trigger that app.",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 14),
        });

        _statusText = new TextBlock
        {
            Text = "Watching for 8 seconds...",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(_statusText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        panel.Children.Add(buttons);

        _openButton = CreateButton("Open app", isPrimary: true, OpenDetectedApp);
        _openButton.IsEnabled = false;
        buttons.Children.Add(_openButton);

        buttons.Children.Add(CreateButton("Close", isPrimary: false, Close));

        Loaded += OnLoaded;
        Closed += (_, _) => _cts.Cancel();
    }

    public static HotkeyOwnerProbe.Result Show(Window owner, string gesture)
    {
        var dialog = new HotkeyOwnerProbeDialog(gesture) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result ?? new HotkeyOwnerProbe.Result(
            false,
            new HotkeyConflictSource(
                "another app on this PC",
                "ms-settings:keyboard",
                false,
                "WinShot could not identify the app that received the hotkey."),
            "WinShot could not identify the app that received the hotkey.");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _result = await HotkeyOwnerProbe.RunAsync(_gesture, TimeSpan.FromSeconds(8), _cts.Token);
            if (_result.Found)
            {
                _statusText.Text = $"Looks like {_result.Source.DisplayName} caught {_gesture}.";
                _openButton.IsEnabled = !string.IsNullOrWhiteSpace(_result.Source.LaunchTarget);
                Activate();
            }
            else
            {
                _statusText.Text = "WinShot could not identify the app. It may be a silent hotkey, or the app did not come forward.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey owner probe failed", ex);
            _statusText.Text = "WinShot could not run the hotkey watcher.";
        }
    }

    private void OpenDetectedApp()
    {
        string? target = _result?.Source.LaunchTarget;
        if (string.IsNullOrWhiteSpace(target))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open '{target}' after hotkey probe", ex);
        }
    }

    private static Button CreateButton(string text, bool isPrimary, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 86,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(isPrimary ? Color.FromRgb(0x2D, 0x7D, 0xFF) : Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Foreground = Brushes.White,
            BorderBrush = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
