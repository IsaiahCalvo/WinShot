using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

public sealed class FastDisplayPickerDialog : WF.Form
{
    private static readonly SD.Color Back = SD.Color.FromArgb(43, 43, 43);
    private static readonly SD.Color ButtonBack = SD.Color.FromArgb(58, 58, 58);
    private static readonly SD.Color ButtonHot = SD.Color.FromArgb(79, 79, 79);
    private static readonly SD.Color Accent = ThemePalette.Accent;
    private static readonly SD.Color TextColor = SD.Color.White;

    private FastDisplayPickerDialog()
    {
        AutoScaleMode = WF.AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = WF.AutoSizeMode.GrowAndShrink;
        BackColor = Back;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Padding = new WF.Padding(18);
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.CenterScreen;
        TopMost = true;

        var panel = new WF.FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            BackColor = Back,
            FlowDirection = WF.FlowDirection.TopDown,
            Margin = WF.Padding.Empty,
            Padding = WF.Padding.Empty,
            WrapContents = false,
        };

        panel.Controls.Add(new WF.Label
        {
            AutoSize = false,
            Font = new SD.Font("Segoe UI", 10f, SD.FontStyle.Bold),
            ForeColor = TextColor,
            Height = 24,
            Margin = new WF.Padding(0, 0, 0, 8),
            Text = "Choose a display",
            Width = 260,
        });

        var screens = WF.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var bounds = screen.Bounds;
            var button = Button(
                $"Display {i + 1}{(screen.Primary ? " (primary)" : "")} - {bounds.Width}x{bounds.Height}",
                260,
                ButtonBack);
            button.Click += (_, _) =>
            {
                SelectedBounds = bounds;
                DialogResult = WF.DialogResult.OK;
            };
            panel.Controls.Add(button);
        }

        var bottom = new WF.FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = WF.AutoSizeMode.GrowAndShrink,
            BackColor = Back,
            FlowDirection = WF.FlowDirection.RightToLeft,
            Margin = new WF.Padding(0, 10, 0, 0),
            Padding = WF.Padding.Empty,
            Width = 260,
            WrapContents = false,
        };

        var cancel = Button("Cancel", 74, ButtonBack);
        cancel.Click += (_, _) => DialogResult = WF.DialogResult.Cancel;
        bottom.Controls.Add(cancel);
        panel.Controls.Add(bottom);

        Controls.Add(panel);
        AcceptButton = screens.Length > 0 ? panel.Controls.OfType<WF.Button>().FirstOrDefault() : null;
        CancelButton = cancel;

        MouseDown += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
                Native.ReleaseCaptureAndDrag(Handle);
        };
        panel.MouseDown += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
                Native.ReleaseCaptureAndDrag(Handle);
        };

        UpdateWindowRegion();
    }

    public SD.Rectangle? SelectedBounds { get; private set; }


    public static SD.Rectangle? ChooseDisplay()
    {
        var screens = WF.Screen.AllScreens;
        if (screens.Length == 1)
            return screens[0].Bounds;

        using var dialog = new FastDisplayPickerDialog();
        PerfLog.TrackFirstShown(dialog, "display picker");
        return dialog.ShowDialog() == WF.DialogResult.OK ? dialog.SelectedBounds : null;
    }

    protected override void OnKeyDown(WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape)
        {
            DialogResult = WF.DialogResult.Cancel;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateWindowRegion();
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        IntPtr regionHandle = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 20, 20);
        Region = SD.Region.FromHrgn(regionHandle);
        Native.DeleteObject(regionHandle);
    }

    private static WF.Button Button(string text, int width, SD.Color backColor)
    {
        var button = new WF.Button
        {
            AutoSize = false,
            BackColor = backColor,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Height = 30,
            Margin = new WF.Padding(0, 0, 0, 6),
            Text = text,
            TextAlign = SD.ContentAlignment.MiddleLeft,
            UseVisualStyleBackColor = false,
            Width = width,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ButtonHot;
        return button;
    }

    private static class Native
    {
        private const int WmNclbuttondown = 0x00A1;
        private static readonly IntPtr HtCaption = new(2);

        public static void ReleaseCaptureAndDrag(IntPtr handle)
        {
            ReleaseCapture();
            SendMessage(handle, WmNclbuttondown, HtCaption, IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
