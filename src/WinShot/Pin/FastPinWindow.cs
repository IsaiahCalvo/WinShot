using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Pin;

public sealed class FastPinWindow : WF.Form
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const int WmNclbuttondown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    private static readonly List<FastPinWindow> OpenPins = new();
    private static int _openCount;

    private readonly SD.Bitmap _image;
    private readonly SettingsService? _settings;
    private readonly int _naturalWidth;
    private readonly int _naturalHeight;
    private readonly WF.ContextMenuStrip _menu;
    private readonly WF.ToolStripMenuItem _lockItem;
    private double _scale;
    private bool _locked;
    private double _opacityBeforeLock = 1.0;
    private bool _mouseInside;

    public FastPinWindow(SD.Bitmap image, SettingsService? settings = null)
    {
        _image = image;
        _settings = settings;
        _naturalWidth = image.Width;
        _naturalHeight = image.Height;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = SD.Color.Black;
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        _menu = new WF.ContextMenuStrip();
        _menu.Items.Add("Copy", null, async (_, _) => await CopyAsync());
        _menu.Items.Add("Save...", null, async (_, _) => await SaveAsync());
        _lockItem = new WF.ToolStripMenuItem("Lock (Ctrl+L)", null, (_, _) => SetLocked(!_locked));
        _menu.Items.Add(_lockItem);
        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add("Close", null, (_, _) => Close());
        ContextMenuStrip = _menu;

        var area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        _scale = Math.Min(1.0, Math.Min(area.Width * 0.6 / _naturalWidth, area.Height * 0.6 / _naturalHeight));
        ApplyScale();

        double offset = (_openCount++ % 8) * 24;
        Location = new SD.Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2) + (int)offset,
            area.Top + Math.Max(0, (area.Height - Height) / 2) + (int)offset);

        MouseEnter += (_, _) => { _mouseInside = true; Invalidate(); };
        MouseLeave += (_, _) => { _mouseInside = false; Invalidate(); };
        MouseDown += OnMouseDown;
        MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
                Close();
        };
        MouseWheel += OnPinMouseWheel;
        KeyDown += OnPinKeyDown;
        Closed += (_, _) =>
        {
            OpenPins.Remove(this);
            _image.Dispose();
            MemoryCleanup.Request();
        };
        OpenPins.Add(this);
    }

    public static void UnlockAllPins()
    {
        foreach (var pin in OpenPins.ToList())
            pin.SetLocked(false);
    }

    public static void Prewarm(SettingsService? settings = null)
    {
        try
        {
            using var bitmap = new SD.Bitmap(1, 1);
            using var pin = new FastPinWindow((SD.Bitmap)bitmap.Clone(), settings)
            {
                Opacity = 0,
                ShowInTaskbar = false,
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-32000, -32000),
            };
            pin.Show();
            WF.Application.DoEvents();
            pin.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Fast pin prewarm failed", ex);
        }
    }

    public static void TrackFirstShown(WF.Form form, string metricName)
    {
        var sw = Stopwatch.StartNew();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null)
                form.Shown -= handler;
            Log.Info($"Perf {metricName} first show: {sw.ElapsedMilliseconds} ms");
        };
        form.Shown += handler;
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(_image, new SD.Rectangle(1, 1, Math.Max(1, ClientSize.Width - 2), Math.Max(1, ClientSize.Height - 2)));

        if (_mouseInside && !_locked)
        {
            using var pen = new SD.Pen(SD.Color.FromArgb(77, 163, 255), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            string label = "drag";
            using var font = new SD.Font("Segoe UI", 8.5f);
            SD.Size size = WF.TextRenderer.MeasureText(label, font);
            var badge = new SD.Rectangle(
                Math.Max(1, (ClientSize.Width - size.Width - 20) / 2),
                6,
                size.Width + 20,
                size.Height + 5);
            using var brush = new SD.SolidBrush(SD.Color.FromArgb(217, 30, 30, 30));
            using var border = new SD.Pen(SD.Color.FromArgb(51, 255, 255, 255));
            e.Graphics.FillRectangle(brush, badge);
            e.Graphics.DrawRectangle(border, badge);
            WF.TextRenderer.DrawText(
                e.Graphics,
                label,
                font,
                new SD.Point(badge.Left + 10, badge.Top + 2),
                SD.Color.White);
        }

        base.OnPaint(e);
    }

    private void ApplyScale()
    {
        ClientSize = new SD.Size(
            Math.Max(1, (int)Math.Round(_naturalWidth * _scale) + 2),
            Math.Max(1, (int)Math.Round(_naturalHeight * _scale) + 2));
    }

    private void OnMouseDown(object? sender, WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left)
            return;

        ReleaseCapture();
        SendMessage(Handle, WmNclbuttondown, HtCaption, IntPtr.Zero);
    }

    private void OnPinMouseWheel(object? sender, WF.MouseEventArgs e)
    {
        if ((ModifierKeys & WF.Keys.Control) == WF.Keys.Control)
        {
            Opacity = PinInteraction.AdjustOpacity(Opacity, e.Delta);
            return;
        }

        double newScale = PinInteraction.AdjustScale(_scale, e.Delta);
        if (Math.Abs(newScale - _scale) < 0.0001)
            return;

        double factor = newScale / _scale;
        Left -= (int)Math.Round(e.X * (factor - 1));
        Top -= (int)Math.Round(e.Y * (factor - 1));
        _scale = newScale;
        ApplyScale();
        Invalidate();
    }

    private void OnPinKeyDown(object? sender, WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape)
        {
            Close();
            return;
        }

        if (e.KeyCode == WF.Keys.L && e.Control)
        {
            SetLocked(!_locked);
            e.Handled = true;
            return;
        }

        int step = PinInteraction.NudgeStep(e.Shift);
        switch (e.KeyCode)
        {
            case WF.Keys.Left: Left -= step; e.Handled = true; break;
            case WF.Keys.Right: Left += step; e.Handled = true; break;
            case WF.Keys.Up: Top -= step; e.Handled = true; break;
            case WF.Keys.Down: Top += step; e.Handled = true; break;
        }
    }

    private void SetLocked(bool locked)
    {
        if (_locked == locked)
            return;

        long style = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        if (locked)
        {
            _opacityBeforeLock = Opacity;
            SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(style | WsExTransparent));
            Opacity = PinInteraction.LockedOpacity(_opacityBeforeLock);
            _mouseInside = false;
        }
        else
        {
            SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(style & ~WsExTransparent));
            Opacity = _opacityBeforeLock;
        }

        _locked = locked;
        _lockItem.Text = locked ? "Unlock (Ctrl+L)" : "Lock (Ctrl+L)";
        Invalidate();
    }

    private async Task CopyAsync()
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

    private async Task SaveAsync()
    {
        try
        {
            string folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinShot");
            System.IO.Directory.CreateDirectory(folder);
            using var dialog = new WF.SaveFileDialog
            {
                FileName = _settings is null
                    ? CaptureService.DefaultFileName("png")
                    : FileNamer.Next(_settings, "png"),
                InitialDirectory = folder,
                Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            var copy = CaptureService.CloneBitmap(_image);
            await Task.Run(() =>
            {
                using (copy)
                    ImageSaver.Save(copy, dialog.FileName);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Pin save failed", ex);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
