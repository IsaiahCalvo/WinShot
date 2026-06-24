using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Pin;

/// <summary>
/// Borderless always-on-top floating image ("pin to screen"). Owns its bitmap
/// (callers pass a clone) and disposes it on close. Mouse wheel resizes around
/// the cursor, Ctrl+wheel adjusts opacity, double-click or Esc closes.
/// Ctrl+L (or the context menu) toggles click-through lock; arrow keys nudge
/// the pin 1 px (Shift+arrow = 10 px).
/// </summary>
public partial class PinWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;

    /// <summary>Cascades successive pins so they don't cover each other exactly.</summary>
    private static int _openCount;

    /// <summary>Every live pin, so <see cref="UnlockAllPins"/> can reach windows
    /// that have become click-through and unreachable with the mouse.</summary>
    private static readonly List<PinWindow> OpenPins = new();

    private readonly SD.Bitmap _image;
    private readonly SettingsService? _settings;
    private readonly double _naturalWidth;
    private readonly double _naturalHeight;
    private double _scale;
    private bool _locked;
    private bool _closed;
    private double _opacityBeforeLock = 1.0;

    public PinWindow(SD.Bitmap image, SettingsService? settings = null)
    {
        InitializeComponent();
        _image = image;
        _settings = settings;
        _naturalWidth = image.Width;
        _naturalHeight = image.Height;
        ContentRendered += OnInitialContentRendered;

        var wa = SystemParameters.WorkArea;
        _scale = Math.Min(1.0, Math.Min(wa.Width * 0.6 / _naturalWidth, wa.Height * 0.6 / _naturalHeight));
        ApplyScale();

        double offset = (_openCount++ % 8) * 24;
        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2) + offset;
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2) + offset;

        OpenPins.Add(this);
        Closed += (_, _) =>
        {
            _closed = true;
            OpenPins.Remove(this);
            _image.Dispose();
        };
    }

    private void OnInitialContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnInitialContentRendered;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!_closed)
                    _ = LoadImageAsync(_image);
            }));
    }

    public static void Prewarm(SettingsService? settings = null)
    {
        try
        {
            var bitmap = new SD.Bitmap(1, 1);
            var window = new PinWindow(bitmap, settings)
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                if (window.IsVisible)
                    window.Close();
            };
            window.ContentRendered += (_, _) =>
            {
                fallback.Stop();
                window.Close();
            };
            window.Show();
            fallback.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Pin prewarm failed", ex);
        }
    }

    /// <summary>True while the pin is click-through (WS_EX_TRANSPARENT).</summary>
    public bool IsLocked => _locked;

    private async Task LoadImageAsync(SD.Bitmap image)
    {
        if (_closed)
            return;

        try
        {
            var source = await CaptureService.ToBitmapSourceSnapshotAsync(image);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_closed)
                    Img.Source = source;
            });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load pinned image", ex);
        }
    }

    /// <summary>
    /// Toggles click-through. A locked pin ignores all mouse input and dims by
    /// ~15% as a visual cue; unlocking restores the previous opacity.
    /// </summary>
    public void SetLocked(bool locked)
    {
        if (_locked == locked) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return; // not shown yet

        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if (locked)
        {
            _opacityBeforeLock = Opacity;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExTransparent));
            Opacity = PinInteraction.LockedOpacity(_opacityBeforeLock);
        }
        else
        {
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style & ~WsExTransparent));
            Opacity = _opacityBeforeLock;
        }
        _locked = locked;
        LockMenuItem.Header = locked ? "Unlock (Ctrl+L)" : "Lock (Ctrl+L)";
    }

    /// <summary>Restores normal interaction on every locked pin. Wired to the
    /// tray menu as the escape hatch for click-through windows.</summary>
    public static void UnlockAllPins()
    {
        foreach (PinWindow pin in OpenPins.ToList())
            pin.SetLocked(false);
    }

    private void ApplyScale()
    {
        Width = _naturalWidth * _scale + 2;   // +2 for the 1px frame border on each side
        Height = _naturalHeight * _scale + 2;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { /* button released mid-call */ }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Opacity = PinInteraction.AdjustOpacity(Opacity, e.Delta);
            return;
        }

        double newScale = PinInteraction.AdjustScale(_scale, e.Delta);
        if (Math.Abs(newScale - _scale) < 0.0001) return;

        // Keep the image point under the cursor stationary while resizing.
        var pos = e.GetPosition(this);
        double factor = newScale / _scale;
        Left -= pos.X * (factor - 1);
        Top -= pos.Y * (factor - 1);
        _scale = newScale;
        ApplyScale();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        if (e.Key == Key.L && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetLocked(!_locked);
            e.Handled = true;
            return;
        }

        double step = PinInteraction.NudgeStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        switch (e.Key)
        {
            case Key.Left: Left -= step; e.Handled = true; break;
            case Key.Right: Left += step; e.Handled = true; break;
            case Key.Up: Top -= step; e.Handled = true; break;
            case Key.Down: Top += step; e.Handled = true; break;
        }
    }

    private void OnToggleLock(object sender, RoutedEventArgs e) => SetLocked(!_locked);

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            await CaptureService.CopyToClipboardAsync(_image);
        }
        catch (Exception ex)
        {
            Log.Error("Pin copy failed", ex);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinShot");
            System.IO.Directory.CreateDirectory(folder);
            var dialog = new SaveFileDialog
            {
                FileName = _settings is null
                    ? CaptureService.DefaultFileName("png")
                    : FileNamer.Next(_settings, "png"),
                InitialDirectory = folder,
                Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp",
            };
            if (dialog.ShowDialog(this) == true)
            {
                var copy = CaptureService.CloneBitmap(_image);
                await Task.Run(() =>
                {
                    using (copy)
                        ImageSaver.Save(copy, dialog.FileName);
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Pin save failed", ex);
        }
    }

    private void OnCloseItem(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
