using System.Runtime.InteropServices;
using ScreenRecorderLib;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastRecordingOptionsDialog : WF.Form
{
    private static readonly SD.Color Back = SD.Color.FromArgb(43, 43, 43);
    private static readonly SD.Color FieldBack = SD.Color.FromArgb(58, 58, 58);
    private static readonly SD.Color TextColor = SD.Color.White;
    private static readonly SD.Color MutedText = SD.Color.FromArgb(220, 220, 220);
    private static readonly SD.Color Accent = ThemePalette.Accent;
    // Same translucent light hairline FastRecordingToastWindow uses for the CleanShot panel look.
    private static readonly SD.Color Border = SD.Color.FromArgb(54, 255, 255, 255);

    private readonly WF.RadioButton _mp4Radio;
    private readonly WF.RadioButton _gifRadio;
    private readonly WF.CheckBox _audioCheck;
    private readonly WF.Label _micDeviceLabel;
    private readonly WF.ComboBox _micDeviceCombo;
    private readonly WF.CheckBox _systemAudioCheck;
    private readonly WF.ComboBox _webcamCombo;
    private readonly WF.Label _webcamDeviceLabel;
    private readonly WF.ComboBox _webcamDeviceCombo;
    private readonly WF.TextBox _webcamSizeBox;

    // DeviceName values parallel to the combo items (index 0 = "Default", which
    // maps to the first real device). Populated lazily from ScreenRecorderLib.
    private readonly List<string?> _micDeviceNames = new();
    private readonly List<string?> _webcamDeviceNames = new();
    private readonly WF.CheckBox _cursorCheck;
    private readonly WF.CheckBox _clickHighlightCheck;
    private readonly WF.CheckBox _keystrokeCheck;
    private readonly WF.TextBox _countdownBox;
    private readonly WF.ComboBox _fpsCombo;
    private readonly WF.ComboBox _qualityCombo;
    private readonly WF.Label _fpsLabel;
    private readonly WF.Label _qualityLabel;
    private readonly WF.Label _gifFpsLabel;
    private readonly WF.ComboBox _gifFpsCombo;
    private static FastRecordingOptionsDialog? _cached;
    private TaskCompletionSource<WF.DialogResult>? _completion;

    public FastRecordingOptionsDialog(Settings settings)
    {
        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        ClientSize = new SD.Size(286, 504);
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.CenterScreen;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        var title = Label("Record screen", 18, 16, 250, bold: true, size: 10);
        Controls.Add(title);

        _mp4Radio = Radio("MP4 video", 18, 48, true);
        _gifRadio = Radio("GIF animation", 18, 74, false);
        Controls.Add(_mp4Radio);
        Controls.Add(_gifRadio);

        // FPS + Quality. The GIF FPS field shares the Quality row but is only
        // shown when GIF is selected (Quality is meaningless for GIF here).
        _fpsLabel = Label("Frame rate", 18, 108, 92);
        Controls.Add(_fpsLabel);
        _fpsCombo = Combo(110, 104, 150);
        _fpsCombo.Items.AddRange(["60 fps", "30 fps", "15 fps"]);
        _fpsCombo.SelectedIndex = 1;
        Controls.Add(_fpsCombo);

        _qualityLabel = Label("Quality", 18, 142, 92);
        Controls.Add(_qualityLabel);
        _qualityCombo = Combo(110, 138, 150);
        _qualityCombo.Items.AddRange(["High", "Medium", "Low"]);
        _qualityCombo.SelectedIndex = 1;
        Controls.Add(_qualityCombo);

        _gifFpsLabel = Label("GIF frame rate", 18, 142, 92);
        Controls.Add(_gifFpsLabel);
        _gifFpsCombo = Combo(110, 138, 150);
        _gifFpsCombo.Items.AddRange(["20 fps", "15 fps", "12 fps", "8 fps"]);
        _gifFpsCombo.SelectedIndex = 2;
        Controls.Add(_gifFpsCombo);

        _audioCheck = Check("Record microphone", 18, 172, isChecked: false);
        Controls.Add(_audioCheck);

        // Microphone device picker — only shown when "Record microphone" is on.
        _micDeviceLabel = Label("Mic device", 18, 202, 92);
        Controls.Add(_micDeviceLabel);
        _micDeviceCombo = Combo(110, 198, 150);
        Controls.Add(_micDeviceCombo);

        _systemAudioCheck = Check("Record system audio", 18, 232, isChecked: false);
        Controls.Add(_systemAudioCheck);

        Controls.Add(Label("Webcam", 18, 266, 92));
        _webcamCombo = new WF.ComboBox
        {
            BackColor = FieldBack,
            DropDownStyle = WF.ComboBoxStyle.DropDownList,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(110, 262),
            Size = new SD.Size(150, 24),
        };
        _webcamCombo.Items.AddRange(["Off", "Top left", "Top right", "Bottom left", "Bottom right", "Fullscreen"]);
        _webcamCombo.SelectedIndex = 0;
        _webcamCombo.SelectedIndexChanged += (_, _) => UpdateMp4DependentState();
        Controls.Add(_webcamCombo);

        // Webcam device picker — only shown when a webcam position is selected.
        _webcamDeviceLabel = Label("Camera", 18, 300, 92);
        Controls.Add(_webcamDeviceLabel);
        _webcamDeviceCombo = Combo(110, 296, 150);
        Controls.Add(_webcamDeviceCombo);

        Controls.Add(Label("Webcam size", 18, 334, 92));
        _webcamSizeBox = new WF.TextBox
        {
            BackColor = FieldBack,
            BorderStyle = WF.BorderStyle.FixedSingle,
            ForeColor = TextColor,
            Location = new SD.Point(110, 330),
            Size = new SD.Size(44, 24),
            Text = RecordingOptions.DefaultWebcamSizePercent.ToString(),
            TextAlign = WF.HorizontalAlignment.Center,
        };
        Controls.Add(_webcamSizeBox);
        Controls.Add(Label("10-45%", 164, 334, 90, color: SD.Color.FromArgb(150, 150, 150), size: 8));

        _cursorCheck = Check("Capture cursor", 18, 368, isChecked: false);
        _clickHighlightCheck = Check("Highlight mouse clicks", 18, 394, isChecked: false);
        _keystrokeCheck = Check("Show keystrokes", 18, 420, isChecked: false);
        Controls.Add(_cursorCheck);
        Controls.Add(_clickHighlightCheck);
        Controls.Add(_keystrokeCheck);

        Controls.Add(Label("Countdown (s)", 18, 454, 92));
        _countdownBox = new WF.TextBox
        {
            BackColor = FieldBack,
            BorderStyle = WF.BorderStyle.FixedSingle,
            ForeColor = TextColor,
            Location = new SD.Point(110, 450),
            Size = new SD.Size(44, 24),
            Text = "0",
            TextAlign = WF.HorizontalAlignment.Center,
        };
        Controls.Add(_countdownBox);
        Controls.Add(Label("0 = off", 164, 454, 90, color: SD.Color.FromArgb(150, 150, 150), size: 8));

        var cancel = Button("Cancel", 110, 482, SD.Color.FromArgb(58, 58, 58));
        cancel.Click += (_, _) => Complete(WF.DialogResult.Cancel);
        Controls.Add(cancel);

        var start = Button("Start", 190, 482, Accent);
        start.Click += (_, _) => Complete(WF.DialogResult.OK);
        Controls.Add(start);
        AcceptButton = start;
        CancelButton = cancel;

        _audioCheck.CheckedChanged += (_, _) => UpdateMp4DependentState();
        _mp4Radio.CheckedChanged += (_, _) => UpdateMp4DependentState();
        _gifRadio.CheckedChanged += (_, _) => UpdateMp4DependentState();
        LoadDevices();
        ApplySettings(settings);

        MouseDown += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
                Native.ReleaseCaptureAndDrag(Handle);
        };
    }

    /// <summary>
    /// Populates the microphone and webcam device pickers from ScreenRecorderLib.
    /// Item 0 of each combo is "Default" (mapped to a null DeviceName so the
    /// recorder uses the system default); real devices follow. Best-effort: if
    /// enumeration fails the combos simply show "Default" only.
    /// </summary>
    private void LoadDevices()
    {
        _micDeviceCombo.Items.Clear();
        _micDeviceNames.Clear();
        _micDeviceCombo.Items.Add("Default");
        _micDeviceNames.Add(null);
        try
        {
            var inputs = Recorder.GetSystemAudioDevices(AudioDeviceSource.InputDevices);
            if (inputs is not null)
            {
                foreach (var device in inputs)
                {
                    if (string.IsNullOrEmpty(device.DeviceName))
                        continue;
                    _micDeviceCombo.Items.Add(Truncate(device.FriendlyName ?? device.DeviceName));
                    _micDeviceNames.Add(device.DeviceName);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to enumerate microphone devices", ex);
        }
        // When exactly one real device exists, default to it; otherwise leave on
        // "Default" so the recorder picks the system default microphone.
        _micDeviceCombo.SelectedIndex = _micDeviceCombo.Items.Count == 2 ? 1 : 0;

        _webcamDeviceCombo.Items.Clear();
        _webcamDeviceNames.Clear();
        try
        {
            var cameras = Recorder.GetSystemVideoCaptureDevices();
            if (cameras is not null)
            {
                foreach (var camera in cameras)
                {
                    if (string.IsNullOrEmpty(camera.DeviceName))
                        continue;
                    _webcamDeviceCombo.Items.Add(Truncate(camera.FriendlyName ?? camera.DeviceName));
                    _webcamDeviceNames.Add(camera.DeviceName);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to enumerate webcam devices", ex);
        }
        if (_webcamDeviceCombo.Items.Count == 0)
        {
            _webcamDeviceCombo.Items.Add("No camera found");
            _webcamDeviceNames.Add(null);
        }
        _webcamDeviceCombo.SelectedIndex = 0;
    }

    private static string Truncate(string text) =>
        text.Length <= 28 ? text : text[..27] + "…";

    public static FastRecordingOptionsDialog Create(Settings settings)
    {
        var dialog = Interlocked.Exchange(ref _cached, null);
        if (dialog is { IsDisposed: false })
        {
            dialog.ApplySettings(settings);
            dialog.Opacity = 1;
            dialog.ShowInTaskbar = false;
            dialog.CenterOnCurrentScreen();
            return dialog;
        }

        return new FastRecordingOptionsDialog(settings);
    }

    public static void Return(FastRecordingOptionsDialog dialog)
    {
        if (dialog.IsDisposed)
            return;

        dialog.DialogResult = WF.DialogResult.None;
        dialog._completion = null;
        dialog.Opacity = 1;
        dialog.Hide();
        if (_cached is null)
            _cached = dialog;
        else
            dialog.Dispose();
    }

    public Task<WF.DialogResult> ShowAsync()
    {
        _completion = new TaskCompletionSource<WF.DialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Show();
        Activate();
        Focus();
        return _completion.Task;
    }

    public bool IsGif => _gifRadio.Checked;

    public bool RecordMicrophone => _audioCheck.Checked && _mp4Radio.Checked;

    public bool RecordSystemAudio => _systemAudioCheck.Checked && _mp4Radio.Checked;

    public bool CaptureCursor => _cursorCheck.Checked;

    public bool ShowClickHighlights => _clickHighlightCheck.Checked;

    public bool ShowKeystrokes => _keystrokeCheck.Checked;

    public int CountdownSeconds =>
        int.TryParse(_countdownBox.Text.Trim(), out int seconds)
            ? RecordingOptions.ClampCountdownSeconds(seconds)
            : RecordingOptions.MinCountdownSeconds;

    public string WebcamPosition => _mp4Radio.Checked
        ? _webcamCombo.SelectedIndex switch
        {
            1 => "top-left",
            2 => "top-right",
            3 => "bottom-left",
            4 => "bottom-right",
            5 => "fullscreen",
            _ => "off",
        }
        : "off";

    public int WebcamSizePercent =>
        int.TryParse(_webcamSizeBox.Text.Trim(), out int percent)
            ? RecordingOptions.ClampWebcamSizePercent(percent)
            : RecordingOptions.DefaultWebcamSizePercent;

    /// <summary>
    /// DeviceName of the chosen microphone, or <c>null</c> to use the system
    /// default. Only meaningful when <see cref="RecordMicrophone"/> is true.
    /// </summary>
    public string? MicrophoneDeviceName =>
        SelectedDeviceName(_micDeviceCombo, _micDeviceNames);

    /// <summary>
    /// DeviceName of the chosen webcam, or <c>null</c> when none is available.
    /// Only meaningful when <see cref="WebcamPosition"/> is not "off".
    /// </summary>
    public string? WebcamDeviceName =>
        SelectedDeviceName(_webcamDeviceCombo, _webcamDeviceNames);

    private static string? SelectedDeviceName(WF.ComboBox combo, List<string?> names)
    {
        int index = combo.SelectedIndex;
        return index >= 0 && index < names.Count ? names[index] : null;
    }

    /// <summary>Chosen MP4 frame rate (fps). Applies to the H.264 recorder.</summary>
    public int RecordingFps => _fpsCombo.SelectedIndex switch
    {
        0 => 60,
        2 => 15,
        _ => 30,
    };

    /// <summary>Chosen GIF frame rate (fps). Only meaningful when <see cref="IsGif"/>.</summary>
    public int GifFps => _gifFpsCombo.SelectedIndex switch
    {
        0 => 20,
        1 => 15,
        3 => 8,
        _ => 12,
    };

    /// <summary>H.264 quality (0–100) mapped from the High/Medium/Low picker.</summary>
    public int VideoQuality => _qualityCombo.SelectedIndex switch
    {
        0 => 85,
        2 => 50,
        _ => 70,
    };

    protected override void OnKeyDown(WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape)
        {
            Complete(WF.DialogResult.Cancel);
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(WF.FormClosingEventArgs e)
    {
        if (_completion is not null)
        {
            e.Cancel = true;
            Complete(WF.DialogResult.Cancel);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateWindowRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new SD.Pen(Border, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        IntPtr regionHandle = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 20, 20);
        Region = SD.Region.FromHrgn(regionHandle);
        DeleteObject(regionHandle);
    }

    private void Complete(WF.DialogResult result)
    {
        DialogResult = result;
        Hide();
        _completion?.TrySetResult(result);
        _completion = null;
    }

    private void ApplySettings(Settings settings)
    {
        _mp4Radio.Checked = true;
        _gifRadio.Checked = false;
        _audioCheck.Checked = settings.RecordAudio;
        _systemAudioCheck.Checked = settings.RecordSystemAudio;
        _cursorCheck.Checked = settings.CaptureCursor;
        _clickHighlightCheck.Checked = settings.ShowClickHighlights;
        _keystrokeCheck.Checked = settings.ShowKeystrokes;
        _countdownBox.Text = RecordingOptions.ClampCountdownSeconds(settings.RecordingCountdownSeconds).ToString();
        _webcamSizeBox.Text = RecordingOptions.ClampWebcamSizePercent(settings.WebcamOverlaySizePercent).ToString();
        _fpsCombo.SelectedIndex = settings.RecordingFps switch
        {
            >= 60 => 0,
            <= 15 => 2,
            _ => 1,
        };
        _gifFpsCombo.SelectedIndex = settings.GifFps switch
        {
            >= 20 => 0,
            >= 14 => 1,
            <= 9 => 3,
            _ => 2,
        };
        _qualityCombo.SelectedIndex = 1;
        _webcamCombo.SelectedIndex = RecordingOptions.NormalizeWebcamPosition(settings.WebcamOverlayPosition) switch
        {
            "top-left" => 1,
            "top-right" => 2,
            "bottom-left" => 3,
            "bottom-right" => 4,
            "fullscreen" => 5,
            _ => 0,
        };
        UpdateMp4DependentState();
    }

    private void CenterOnCurrentScreen()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        Location = new SD.Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private void UpdateMp4DependentState()
    {
        bool mp4 = _mp4Radio.Checked;
        _audioCheck.Enabled = mp4;
        _systemAudioCheck.Enabled = mp4;
        _webcamCombo.Enabled = mp4;
        _webcamSizeBox.Enabled = mp4;

        // MP4 shows FPS + Quality; GIF replaces Quality with a GIF FPS picker.
        _fpsLabel.Visible = mp4;
        _fpsCombo.Visible = mp4;
        _qualityLabel.Visible = mp4;
        _qualityCombo.Visible = mp4;
        _gifFpsLabel.Visible = !mp4;
        _gifFpsCombo.Visible = !mp4;

        // Device pickers only matter for MP4, and only when their feature is on.
        bool micOn = mp4 && _audioCheck.Checked;
        _micDeviceLabel.Visible = micOn;
        _micDeviceCombo.Visible = micOn;

        bool webcamOn = mp4 && _webcamCombo.SelectedIndex > 0;
        _webcamDeviceLabel.Visible = webcamOn;
        _webcamDeviceCombo.Visible = webcamOn;
    }

    private static WF.Label Label(
        string text,
        int x,
        int y,
        int width,
        bool bold = false,
        float size = 9,
        SD.Color? color = null) =>
        new()
        {
            AutoSize = false,
            Font = new SD.Font("Segoe UI", size, bold ? SD.FontStyle.Bold : SD.FontStyle.Regular),
            ForeColor = color ?? MutedText,
            Location = new SD.Point(x, y),
            Size = new SD.Size(width, 22),
            Text = text,
        };

    private static WF.ComboBox Combo(int x, int y, int width) =>
        new()
        {
            BackColor = FieldBack,
            DropDownStyle = WF.ComboBoxStyle.DropDownList,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(x, y),
            Size = new SD.Size(width, 24),
        };

    private static WF.RadioButton Radio(string text, int x, int y, bool isChecked) =>
        new()
        {
            AutoSize = true,
            Checked = isChecked,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(x, y),
            Text = text,
        };

    private static WF.CheckBox Check(string text, int x, int y, bool isChecked) =>
        new()
        {
            AutoSize = true,
            Checked = isChecked,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(x, y),
            Text = text,
        };

    private static WF.Button Button(string text, int x, int y, SD.Color backColor)
    {
        var button = new WF.Button
        {
            BackColor = backColor,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(x, y),
            Size = new SD.Size(70, 26),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private static class Native
    {
        private const int WmNclbuttondown = 0x00A1;
        private static readonly IntPtr HtCaption = new(2);

        public static void ReleaseCaptureAndDrag(IntPtr handle)
        {
            ReleaseCapture();
            SendMessage(handle, WmNclbuttondown, HtCaption, IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
