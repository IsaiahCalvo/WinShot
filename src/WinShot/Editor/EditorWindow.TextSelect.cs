using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Ocr;
using SD = System.Drawing;

namespace WinShot.Editor;

/// <summary>
/// Text select (Shift+V): Survey's third Select mode, beside Box and Lasso. Survey's picks the
/// PDF's own words off the page; a screenshot has no words, so WinShot reads them with the
/// Windows OCR engine the moment the mode is armed and then behaves like Live Text on a Mac,
/// or selecting text anywhere: drag across the words to pick them (whole lines in between
/// come along), double-click a word, triple-click a line, Shift+click to extend, Ctrl+A for
/// all of it. Ctrl+C copies, and a small bar beside the pick offers Copy and Copy all.
///
/// Every recognised word gets a faint tint while the mode is on, so it is plain at a glance
/// which text WinShot can read — the thing Isaiah could not find. The tint and the pick are
/// drawn on the interaction layer, never on the picture, so nothing here is saved or undone.
/// </summary>
public partial class EditorWindow
{
    // Survey's selection blue (#4A90E2): a quiet wash on every readable word, a stronger one
    // on the picked text.
    private static readonly Brush TextWordTint = Frozen(Color.FromArgb(0x29, 0x4A, 0x90, 0xE2));
    private static readonly Brush TextPickTint = Frozen(Color.FromArgb(0x70, 0x4A, 0x90, 0xE2));

    /// <summary>A press must travel this far (screen px) before it is a drag, not a click.</summary>
    private const double TextDragThresholdPx = 3;

    private bool _textSelectMode;
    private IReadOnlyList<TextWord>? _textWords;
    private SD.Bitmap? _textWordsSource;
    private SD.Bitmap? _textReadingSource;
    private bool _textReading;
    private int _textReadGeneration;
    private string? _textError;

    private int _textAnchor = -1;
    private int _textFocus = -1;
    private bool _textDragging;
    private bool _textDragMoved;
    private bool _textShiftExtend;
    private int _textPressWord = -1;
    private Point _textPressPos;

    private Canvas? _textLayer;
    private Path? _textWordPath;
    private Path? _textPickPath;
    private Border? _textBar;
    private TextBlock? _textBarStatus;
    private Button? _textBarCopy;
    private Button? _textBarCopyAll;
    private bool _textBarQueued;
    private bool _textSelectWired;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private (int Start, int End)? PickedTextRange =>
        _textWords is { Count: > 0 } ? ImageTextSelection.Range(_textAnchor, _textFocus) : null;

    /// <summary>Hooked once, from the Select button's menu wiring (it runs on Loaded).</summary>
    private void WireTextSelect()
    {
        if (_textSelectWired) return;
        _textSelectWired = true;
        // Picking any other tool puts Select down, and Text select goes with it: V brings the
        // box back, Shift+V the text, as in Survey.
        SelectToolBtn.Unchecked += (_, _) => LeaveTextSelect();
        ViewTranslate.Changed += (_, _) => QueueTextBarUpdate();
        ViewScale.Changed += (_, _) => QueueTextBarUpdate();
        Viewport.SizeChanged += (_, _) => QueueTextBarUpdate();
    }

    /// <summary>Arms Select in its Text mode and starts reading the picture if it has not been read.</summary>
    private void EnterTextSelect()
    {
        if (_sourceOperationActive) return;
        WireTextSelect();
        _lassoSelect = false;
        _textSelectMode = true;
        CheckToolButton(EditorTool.Select);
        // A mark picked under Box select would keep its handles and quick bar over the text.
        Select(null);
        SyncSelectModeButtons();
        SelectToolBtn.ToolTip = ToolTipWithKey("Text select", ActionBadge(EditorToolKeys.ActionTextSelect));
        System.Windows.Automation.AutomationProperties.SetName(SelectToolBtn, "Text select");

        EnsureTextLayer();
        _textLayer!.Visibility = Visibility.Visible;
        UpdateContextPanels();
        RenderTextLayer();
        UpdateTextBar();
        _ = ReadTextAsync();
    }

    /// <summary>Back to Box or Lasso (or another tool): the tint, the pick and the bar all go.</summary>
    private void LeaveTextSelect()
    {
        if (!_textSelectMode) return;
        _textSelectMode = false;
        if (_textDragging)
        {
            _textDragging = false;
            Viewport.ReleaseMouseCapture();
        }
        _textAnchor = _textFocus = -1;
        if (_textLayer is not null) _textLayer.Visibility = Visibility.Collapsed;
        HideTextBar();
        SyncSelectModeButtons();
        SelectToolBtn.ToolTip = ToolTipWithKey("Select", EditorToolKeys.BadgeFor(EditorTool.Select));
        System.Windows.Automation.AutomationProperties.SetName(SelectToolBtn, "Select");
        UpdateCursor();
    }

    /// <summary>
    /// The picture changed under the words (crop, rotate, flip, resize, or an undo of one):
    /// what was read no longer lines up, so it is read again if the mode is on.
    /// </summary>
    private void OnTextSourceChanged()
    {
        _textWords = null;
        _textWordsSource = null;
        _textReadingSource = null;
        _textReading = false;
        _textError = null;
        _textReadGeneration++;
        _textAnchor = _textFocus = -1;
        if (!_textSelectMode) return;
        RenderTextLayer();
        UpdateTextBar();
        _ = ReadTextAsync();
    }

    private async Task ReadTextAsync()
    {
        SD.Bitmap source = _source;
        if (_textWords is not null && ReferenceEquals(_textWordsSource, source)) return;
        if (_textReading && ReferenceEquals(_textReadingSource, source)) return;

        int generation = ++_textReadGeneration;
        _textReading = true;
        _textReadingSource = source;
        _textError = null;
        UpdateTextBar();

        IReadOnlyList<TextWord> words = Array.Empty<TextWord>();
        string? error = null;
        SD.Bitmap? copy = null;
        try
        {
            // The read runs off the UI thread, and the editor can crop or rotate the picture
            // meanwhile, so the engine gets its own copy.
            copy = source.Clone(new SD.Rectangle(0, 0, source.Width, source.Height),
                SD.Imaging.PixelFormat.Format32bppArgb);
            words = ImageTextSelection.FromOcr(await OcrService.ExtractWordLayoutAsync(copy));
            if (words.Count == 0) error = "No text found in this picture.";
        }
        catch (OcrUnavailableException ex)
        {
            Log.Info($"Text select: {ex.Message}");
            error = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("Text select could not read the picture", ex);
            error = "WinShot could not read the text in this picture.";
        }
        finally
        {
            copy?.Dispose();
        }

        if (_closed || generation != _textReadGeneration) return;
        _textReading = false;
        _textReadingSource = null;
        _textWords = words;
        _textWordsSource = source;
        _textError = error;
        _textAnchor = _textFocus = -1;
        if (!_textSelectMode) return;
        RenderTextLayer();
        UpdateTextBar();
    }

    // ------------------------------------------------------------------ pointer

    private void TextSelectPointerDown(Point pos, PointerArgs e)
    {
        e.Handled = true;
        IReadOnlyList<TextWord>? words = _textWords;
        if (words is not { Count: > 0 })
        {
            _ = ReadTextAsync();
            return;
        }

        int hit = ImageTextSelection.Hit(words, pos);
        if (e.ClickCount >= 3 && hit >= 0)
        {
            (_textAnchor, _textFocus) = ImageTextSelection.LineOf(words, hit);
            RenderTextPick();
            UpdateTextBar();
            return;
        }
        if (e.ClickCount == 2 && hit >= 0)
        {
            _textAnchor = _textFocus = hit;
            RenderTextPick();
            UpdateTextBar();
            return;
        }

        _textDragging = true;
        _textDragMoved = false;
        _textPressPos = pos;
        _textPressWord = ImageTextSelection.Nearest(words, pos);
        _textShiftExtend = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _textAnchor >= 0;
        if (_textShiftExtend)
        {
            _textFocus = _textPressWord;
            RenderTextPick();
        }
        Viewport.CaptureMouse();
        HideTextBar();
    }

    private void TextSelectPointerMove(Point pos)
    {
        IReadOnlyList<TextWord>? words = _textWords;
        if (_textDragging)
        {
            if (words is not { Count: > 0 }) return;
            if (!_textDragMoved)
            {
                if ((pos - _textPressPos).Length * _zoom < TextDragThresholdPx) return;
                _textDragMoved = true;
                if (!_textShiftExtend) _textAnchor = _textPressWord;
            }
            int focus = ImageTextSelection.Nearest(words, pos);
            if (focus != _textFocus)
            {
                _textFocus = focus;
                RenderTextPick();
            }
            return;
        }
        if (_spaceDown || _panning) return;
        bool overText = words is { Count: > 0 } && ImageTextSelection.Hit(words, pos) >= 0;
        SetSurfaceCursor(overText ? Cursors.IBeam : Cursors.Arrow);
    }

    private void TextSelectPointerUp()
    {
        _textDragging = false;
        Viewport.ReleaseMouseCapture();
        // A plain click drops the pick, as a click in text does everywhere else.
        if (!_textDragMoved && !_textShiftExtend) _textAnchor = _textFocus = -1;
        RenderTextPick();
        UpdateTextBar();
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Ctrl+C under Text select. False when nothing is picked, so Ctrl+C keeps copying the picture.</summary>
    private bool TryCopyPickedText()
    {
        if (!_textSelectMode || PickedTextRange is null) return false;
        CopyText(all: false);
        return true;
    }

    private void SelectAllText()
    {
        if (_textWords is not { Count: > 0 } words) return;
        _textAnchor = 0;
        _textFocus = words.Count - 1;
        RenderTextPick();
        UpdateTextBar();
    }

    /// <summary>Esc under Text select: drops the pick. False when there was none.</summary>
    private bool ClearPickedText()
    {
        if (PickedTextRange is null) return false;
        _textAnchor = _textFocus = -1;
        RenderTextPick();
        UpdateTextBar();
        return true;
    }

    private async void CopyText(bool all)
    {
        if (_textWords is not { Count: > 0 } words) return;
        (int start, int end) = all ? (0, words.Count - 1) : PickedTextRange ?? (0, -1);
        if (end < start) return;
        string text = ImageTextSelection.TextFor(words, start, end, _settings.Current.OcrJoinLines);
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            await CaptureService.SetTextToClipboardAsync(text);
        }
        catch (Exception ex)
        {
            Log.Error("Text select could not copy to the clipboard", ex);
            App.Toast("Copy text", "The clipboard is busy. Try again.", severity: ToastSeverity.Error);
            return;
        }
        // The same title and preview every other text grab shows.
        if (OcrClipboardFormatter.Build(new OcrCaptureResult(text, new List<string>())) is { } payload)
            App.Toast(payload.BalloonTitle, payload.Preview, severity: ToastSeverity.Success);
    }

    /// <summary>Right-click under Text select: the text commands, never the mark menu.</summary>
    private void ShowTextSelectMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var itemStyle = (Style)FindResource("DarkMenuItem");
        bool any = _textWords is { Count: > 0 };
        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C", Style = itemStyle, IsEnabled = PickedTextRange is not null };
        var copyAll = new MenuItem { Header = "Copy all text", Style = itemStyle, IsEnabled = any };
        var selectAll = new MenuItem { Header = "Select all text", InputGestureText = "Ctrl+A", Style = itemStyle, IsEnabled = any };
        copy.Click += (_, _) => CopyText(all: false);
        copyAll.Click += (_, _) => CopyText(all: true);
        selectAll.Click += (_, _) => SelectAllText();
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        menu.Items.Add(selectAll);
        menu.PlacementTarget = Viewport;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ drawing

    private void EnsureTextLayer()
    {
        if (_textLayer is not null) return;
        _textWordPath = new Path { Fill = TextWordTint, IsHitTestVisible = false };
        _textPickPath = new Path { Fill = TextPickTint, IsHitTestVisible = false };
        _textLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _textLayer.Children.Add(_textWordPath);
        _textLayer.Children.Add(_textPickPath);
        // Under every other piece of interaction chrome, so handles and guides stay on top.
        InteractionCanvas.Children.Insert(0, _textLayer);

        _textBar = BuildTextBar();
        OverlayCanvas.Children.Add(_textBar);
    }

    private void RenderTextLayer()
    {
        if (_textWordPath is null) return;
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (_textWords is { } words)
            foreach (TextWord w in words)
                group.Children.Add(Rounded(w.Bounds, 1));
        group.Freeze();
        _textWordPath.Data = group;
        RenderTextPick();
    }

    private void RenderTextPick()
    {
        if (_textPickPath is null) return;
        // Nonzero, or two overlapping line boxes would punch a hole where they meet.
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (_textWords is { } words && PickedTextRange is var (start, end))
            foreach (Rect band in ImageTextSelection.Bands(words, start, end))
                group.Children.Add(Rounded(band, 2));
        group.Freeze();
        _textPickPath.Data = group;
    }

    private static RectangleGeometry Rounded(Rect r, double pad)
    {
        r.Inflate(pad, pad);
        return new RectangleGeometry(r, 2, 2);
    }

    // ------------------------------------------------------------------ the bar

    private bool IsTextBarSource(object? source) =>
        _textBar is not null && source is DependencyObject d && (ReferenceEquals(_textBar, d) || _textBar.IsAncestorOf(d));

    private Border BuildTextBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _textBarStatus = new TextBlock
        {
            FontSize = 12,
            FontFamily = (FontFamily)FindResource("SurveyArialFont"),
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("ChromeTextBrush"),
        };
        row.Children.Add(_textBarStatus);
        _textBarCopy = TextBarButton("Copy", "Copy the picked text (Ctrl+C)", () => CopyText(all: false));
        _textBarCopyAll = TextBarButton("Copy all", "Copy all the text in the picture", () => CopyText(all: true));
        row.Children.Add(_textBarCopy);
        row.Children.Add(_textBarCopyAll);

        var bar = new Border
        {
            Height = 36,
            Padding = new Thickness(2, 0, 2, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Brush)FindResource("ChromeBgBrush"),
            BorderBrush = (Brush)FindResource("ChromeHairlineBrush"),
            Visibility = Visibility.Collapsed,
            Child = row,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 4, Direction = 270, Opacity = 0.3 },
        };
        System.Windows.Automation.AutomationProperties.SetName(bar, "Text actions");
        // A click on the bar must never reach the canvas underneath, where it would drop the pick.
        bar.PreviewMouseRightButtonUp += (_, e) => e.Handled = true;
        bar.MouseDown += (_, e) => e.Handled = true;
        return bar;
    }

    private Button TextBarButton(string label, string tip, Action act)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("CompactToolIconButton"),
            Content = new TextBlock
            {
                Text = label,
                // The button style carries the icon font; a word needs the UI face.
                FontFamily = (FontFamily)FindResource("SurveyArialFont"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            },
            ToolTip = tip,
            Focusable = false,
            Width = double.NaN,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(1, 0, 1, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, label);
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            act();
        };
        return btn;
    }

    private void QueueTextBarUpdate()
    {
        if (!_textSelectMode || _textBarQueued || _closed) return;
        _textBarQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _textBarQueued = false;
            UpdateTextBar();
        }, DispatcherPriority.Background);
    }

    private void HideTextBar()
    {
        if (_textBar is not null) _textBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// What the bar says: "Finding text…" while the picture is read, why nothing can be picked
    /// when nothing can, how to pick (with Copy all) before anything is picked, and Copy /
    /// Copy all just above the pick once there is one. Hidden mid-drag.
    /// </summary>
    private void UpdateTextBar()
    {
        if (_textBar is null || _textBarStatus is null || _textBarCopy is null || _textBarCopyAll is null) return;
        if (!_textSelectMode || _textDragging || _closed)
        {
            HideTextBar();
            return;
        }

        (int Start, int End)? picked = PickedTextRange;
        string? status;
        bool copy = false, copyAll = false;
        if (_textReading) status = "Finding text…";
        else if (_textError is not null) status = _textError;
        else if (_textWords is not { Count: > 0 }) status = null;
        else if (picked is null)
        {
            status = "Drag across text to select it";
            copyAll = true;
        }
        else
        {
            status = null;
            copy = copyAll = true;
        }

        if (status is null && !copy && !copyAll)
        {
            HideTextBar();
            return;
        }
        _textBarStatus.Text = status ?? string.Empty;
        _textBarStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        _textBarCopy.Visibility = copy ? Visibility.Visible : Visibility.Collapsed;
        _textBarCopyAll.Visibility = copyAll ? Visibility.Visible : Visibility.Collapsed;

        _textBar.Visibility = Visibility.Visible;
        _textBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = _textBar.DesiredSize;
        var bounds = new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight);

        if (picked is var (start, end) && _textWords is { } words)
        {
            Rect union = Rect.Empty;
            foreach (Rect band in ImageTextSelection.Bands(words, start, end)) union.Union(band);
            if (!union.IsEmpty)
            {
                Rect screen = AnnotationCanvas.TransformToVisual(OverlayCanvas).TransformBounds(union);
                screen.Inflate(4, 4);
                if (SelectionQuickBarLayout.Position(screen, Array.Empty<Rect>(), false, bounds,
                        size.Width, size.Height) is var (left, top, _))
                {
                    Canvas.SetLeft(_textBar, left);
                    Canvas.SetTop(_textBar, top);
                    return;
                }
            }
        }

        // Nothing picked (or the pick is scrolled away): bottom centre, clear of the picture's middle.
        Canvas.SetLeft(_textBar, Math.Max(8, (bounds.Width - size.Width) / 2));
        Canvas.SetTop(_textBar, Math.Max(8, bounds.Height - size.Height - 16));
    }
}
