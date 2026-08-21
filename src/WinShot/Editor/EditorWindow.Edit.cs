using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using WinShot.Editor.Background;
using SD = System.Drawing;

namespace WinShot.Editor;

public partial class EditorWindow : Window
{
    // ------------------------------------------------- rotate / flip / resize

    private void OnRotateCw(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        ApplySourceTransform(SD.RotateFlipType.Rotate90FlipNone);
    }

    private void OnRotateCcw(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        ApplySourceTransform(SD.RotateFlipType.Rotate270FlipNone);
    }

    private void OnFlipHorizontal(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        ApplySourceTransform(SD.RotateFlipType.RotateNoneFlipX);
    }

    private void OnFlipVertical(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        ApplySourceTransform(SD.RotateFlipType.RotateNoneFlipY);
    }

    private void OnResizeImage(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        CommitText();
        CommitPendingCurve();
        var dialog = new ResizeDialog(_source.Width, _source.Height) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        int w = dialog.ResultWidth, h = dialog.ResultHeight;
        if (w == _source.Width && h == _source.Height) return;
        ApplySourceTransform(src => BitmapEffects.Resize(src, w, h));
    }

    private void ApplySourceTransform(SD.RotateFlipType type) =>
        ApplySourceTransform(src => SourceImageTransform.RotateFlip(src, type));

    /// <summary>
    /// Replaces the source bitmap with a transformed copy (rotate/flip/resize) as a
    /// SINGLE compound undo entry. If vector annotations exist, the user confirms and
    /// they are flattened into the image first; undo restores both the previous bitmap
    /// and the live annotations in one step.
    /// </summary>
    private async void ApplySourceTransform(Func<SD.Bitmap, SD.Bitmap> transform)
    {
        if (_sourceOperationActive) return;
        using IDisposable sourceOperation = _sourceLifetime.Acquire();
        CommitText();
        CommitPendingCurve();
        ClearCropPreview();

        var annotations = AnnotationCanvas.Children.Cast<UIElement>().ToList();
        if (annotations.Count > 0 &&
            MessageBox.Show(this,
                "Your annotations will be flattened into the image before this operation. " +
                "They will no longer be editable as separate objects. Continue?",
                "WinShot", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        Select(null);
        SD.Bitmap before = _source;
        SD.Bitmap flat = annotations.Count > 0 ? Flatten() : before;
        SD.Bitmap after;
        _sourceOperationActive = true;
        Cursor = Cursors.Wait;
        try
        {
            after = await Task.Run(() => transform(flat));
        }
        catch (Exception ex)
        {
            Log.Error("Source transform failed", ex);
            return;
        }
        finally
        {
            _sourceOperationActive = false;
            if (!_closed)
                UpdateCursor();
            if (!ReferenceEquals(flat, before)) flat.Dispose(); // temp flatten only
        }
        if (_closed)
        {
            after.Dispose();
            return;
        }
        _owned.Add(after);

        foreach (var el in annotations)
            AnnotationCanvas.Children.Remove(el);
        _source = after;
        await OnSourceReplacedAsync();
        if (_closed) return;

        Push(new EditorAction(
            undo: async () =>
            {
                _source = before;
                foreach (var el in annotations)
                    if (!AnnotationCanvas.Children.Contains(el))
                        AnnotationCanvas.Children.Add(el);
                await OnSourceReplacedAsync();
            },
            redo: async () =>
            {
                foreach (var el in annotations)
                    AnnotationCanvas.Children.Remove(el);
                _source = after;
                await OnSourceReplacedAsync();
            }), apply: false);
    }

    private void ShowDragRect(Rect r, bool dim)
    {
        Canvas.SetLeft(DragRect, r.X);
        Canvas.SetTop(DragRect, r.Y);
        DragRect.Width = r.Width;
        DragRect.Height = r.Height;
        DragRect.Visibility = Visibility.Visible;
        if (dim)
        {
            CropDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, _source.Width, _source.Height)),
                new RectangleGeometry(r));
            CropDim.Visibility = Visibility.Visible;
        }
    }

    private void HideDragRect()
    {
        DragRect.Visibility = Visibility.Collapsed;
        CropDim.Visibility = Visibility.Collapsed;
    }

    private void ClearCropPreview()
    {
        _pendingCrop = null;
        _adjustingCrop = false;
        _activeHandle = -1;
        if (_handleKind == HandleKind.Crop) _handleKind = HandleKind.None;
        HideDragRect();
        HideHandles();
        CropPanel.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------- undo/redo

    private void Push(EditorAction action, bool apply = true)
    {
        if (apply) action.Redo();
        _undoStack.Push(action);
        DiscardRedoStack();
        UpdateUndoRedoButtons();
    }

    /// <summary>Clears the redo stack, letting each dropped action release any
    /// resources it can no longer replay (e.g. blur/pixelate backup bitmaps).</summary>
    private void DiscardRedoStack()
    {
        while (_redoStack.Count > 0)
            _redoStack.Pop().Discard();
    }

    private void PushAddElement(UIElement element)
    {
        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(element),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(element))
                    AnnotationCanvas.Children.Add(element);
            }), apply: false);
    }

    private async void OnUndo(object sender, RoutedEventArgs e) => await UndoAsync();
    private async void OnRedo(object sender, RoutedEventArgs e) => await RedoAsync();

    private async Task UndoAsync()
    {
        if (_sourceOperationActive || _dragging || _movingSelection || _draggingCurveHandle) return;
        CommitText();
        CommitPendingCurve();
        ClearCropPreview();
        Select(null); // the undone action may remove or reshape the selected element
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        await action.UndoAsync();
        _redoStack.Push(action);
        UpdateUndoRedoButtons();
    }

    private async Task RedoAsync()
    {
        if (_sourceOperationActive || _dragging || _movingSelection || _draggingCurveHandle) return;
        CommitPendingCurve(); // a pending curve is a new edit: it clears redo, like any other
        if (_redoStack.Count == 0) return;
        Select(null);
        var action = _redoStack.Pop();
        await action.RedoAsync();
        _undoStack.Push(action);
        UpdateUndoRedoButtons();
    }

    private void UpdateUndoRedoButtons()
    {
        BtnUndo.IsEnabled = _undoStack.Count > 0;
        BtnRedo.IsEnabled = _redoStack.Count > 0;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Let an open text annotation keep its own keyboard behavior.
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.Space)
        {
            if (!_spaceDown)
            {
                _spaceDown = true;
                UpdateCursor();
            }
            e.Handled = true; // keep Space from clicking a focused toolbar button
            return;
        }
        // Z-order on the selected annotation: Ctrl+] / Ctrl+[ move it one step forward/back;
        // add Shift to send it all the way to front/back (the standard + CleanShot convention).
        if (_selected is not null && (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool toFront = e.Key == Key.OemCloseBrackets; // ']' raises, '[' lowers
            ReorderSelected(toFront ? (shift ? ZMove.Front : ZMove.Forward)
                                    : (shift ? ZMove.Back : ZMove.Backward));
            e.Handled = true;
            return;
        }

        EditorShortcutCommand shortcut = EditorShortcut.Resolve(e.Key, Keyboard.Modifiers);
        if (shortcut != EditorShortcutCommand.None)
        {
            switch (shortcut)
            {
                case EditorShortcutCommand.Undo: _ = UndoAsync(); break;
                case EditorShortcutCommand.Redo: _ = RedoAsync(); break;
                case EditorShortcutCommand.FitAndCenter: FitToView(); break;
                case EditorShortcutCommand.Copy: OnCopy(this, new RoutedEventArgs()); break;
                case EditorShortcutCommand.SaveAs: OnSave(this, new RoutedEventArgs()); break;
            }
            e.Handled = true;
            return;
        }

        // Preserve the existing rule that modified letters never activate drawing tools.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;

        // Arrow keys nudge the selection by 1px (10px with Shift). Allowed with a bare
        // Shift modifier only; any other modifier falls through.
        bool onlyShiftOrNone = (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None;
        if (onlyShiftOrNone && _selected is not null &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            (double dx, double dy) = e.Key switch
            {
                Key.Left => (-step, 0.0),
                Key.Right => (step, 0.0),
                Key.Up => (0.0, -step),
                _ => (0.0, step),
            };
            NudgeSelected(dx, dy);
            e.Handled = true;
            return;
        }

        // Single-letter tool shortcuts (no modifiers). Ignored while a curved arrow is
        // mid-edit so Escape/commit stays the only way out of that transient mode.
        if (Keyboard.Modifiers == ModifierKeys.None && _pendingCurve is null && TrySelectToolByKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            if (_selected is not null) { DeleteSelected(); e.Handled = true; }
        }
        else if (e.Key == Key.Escape)
        {
            if (_movingSelection)
            {
                AbortMove();
                Viewport.ReleaseMouseCapture();
                e.Handled = true;
            }
            else if (_draggingCurveHandle)
            {
                _draggingCurveHandle = false;
                Viewport.ReleaseMouseCapture();
                CancelPendingCurve();
                e.Handled = true;
            }
            else if (_pendingCurve is not null)
            {
                CancelPendingCurve();
                e.Handled = true;
            }
            else if (_selected is not null)
            {
                Select(null);
                e.Handled = true;
            }
            else if (_pendingCrop is not null)
            {
                ClearCropPreview();
                e.Handled = true;
            }
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key != Key.Space || Keyboard.FocusedElement is TextBox) return;
        if (_spaceDown)
        {
            _spaceDown = false;
            if (!_panning) UpdateCursor();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------ image annotations

    /// <summary>"Add image…" toolbar button: inserts picked files at the viewport center.</summary>
    private async void OnAddImage(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        CommitText();
        CommitPendingCurve();
        var dialog = new OpenFileDialog
        {
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        await InsertImageFilesAsync(dialog.FileNames, ViewportCenterInContent());
    }

    /// <summary>Opens the existing local background composer with a source-resolution flattened clone.</summary>
    private void OnAddBackground(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        try
        {
            var composer = new BackgroundComposerWindow(Flatten(), _settings, _history);
            composer.Show();
        }
        catch (Exception ex)
        {
            Log.Error("Editor background composer failed", ex);
        }
    }

    private void OnEditorDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnEditorDrop(object sender, DragEventArgs e)
    {
        if (_sourceOperationActive) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;
        CommitText();
        CommitPendingCurve();
        await InsertImageFilesAsync(files, e.GetPosition(AnnotationCanvas));
        e.Handled = true;
    }

    /// <summary>Content-space point currently at the middle of the viewport.</summary>
    private Point ViewportCenterInContent() => new(
        (Viewport.ActualWidth / 2 - ViewTranslate.X) / _zoom,
        (Viewport.ActualHeight / 2 - ViewTranslate.Y) / _zoom);

    /// <summary>
    /// Inserts each decodable file as an image annotation centered at
    /// <paramref name="dropPoint"/> (cascaded slightly for multiple files),
    /// then switches to Select with the last one selected so it can be moved.
    /// </summary>
    private async Task InsertImageFilesAsync(IEnumerable<string> files, Point dropPoint)
    {
        Image? last = null;
        int placed = 0;
        foreach (string file in files.ToList())
        {
            var src = await Task.Run(() => ProjectSerializer.LoadImageFile(file));
            if (src is null) continue; // not an image; already logged
            if (!IsVisible) return;
            last = InsertImageAnnotation(src, new Point(dropPoint.X + placed * 24, dropPoint.Y + placed * 24));
            placed++;
        }
        if (last is null) return;
        CheckToolButton(EditorTool.Select);
        Select(last);
    }

    /// <summary>
    /// Adds one image as a movable/selectable/deletable annotation, centered on a
    /// content point, at natural size capped to 50% of the source image's smaller
    /// dimension. Undo-aware like every other annotation.
    /// </summary>
    private Image InsertImageAnnotation(BitmapSource src, Point center)
    {
        center = ClampToSurface(center);
        double cap = Math.Min(_source.Width, _source.Height) * 0.5;
        double scale = Math.Min(1.0, cap / Math.Max(src.PixelWidth, (double)src.PixelHeight));
        double w = Math.Max(1, src.PixelWidth * scale);
        double h = Math.Max(1, src.PixelHeight * scale);

        var img = new Image { Source = src, Width = w, Height = h, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        double left = center.X - w / 2, top = center.Y - h / 2;
        Canvas.SetLeft(img, left);
        Canvas.SetTop(img, top);
        img.Tag = AnnotationData.ForImage(new Rect(left, top, w, h));

        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(img),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(img))
                    AnnotationCanvas.Children.Add(img);
            }));
        return img;
    }

    // ------------------------------------------------------- copy/save/close

    /// <summary>
    /// Flattens at identity transform: RenderVisual snapshots CanvasHost in its
    /// own coordinate space at the source bitmap's pixel size, so the viewport's
    /// zoom/pan (applied on the EditorSurface ancestor) never affects the output,
    /// and the selection/crop chrome lives on InteractionCanvas outside CanvasHost.
    /// </summary>
    private SD.Bitmap Flatten()
    {
        CommitText();
        CommitPendingCurve();
        CanvasHost.UpdateLayout();
        return BitmapEffects.RenderVisual(CanvasHost, _source.Width, _source.Height);
    }

    // ------------------------------------------------- drag-out ("Drag Me")

    private Point _dragOutStart;
    private bool _dragOutArmed;

    private void OnDragOutMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragOutArmed = true;
        _dragOutStart = e.GetPosition(this);
    }

    /// <summary>
    /// Press-and-drag the bottom-bar pill to drop the flattened image into any app as a real
    /// PNG file (FileDrop), so mail clients attach it instead of pasting it inline.
    /// </summary>
    private async void OnDragOutMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragOutArmed || e.LeftButton != MouseButtonState.Pressed) return;

        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragOutStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragOutStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragOutArmed = false;
        if (_sourceOperationActive) return;
        try
        {
            var flat = Flatten();
            var preview = new DragPreview(flat);
            string path = await WriteDragFileAsync(flat);
            if (!IsVisible) { preview.Dispose(); return; }

            // The preview window is ours to move — OLE pumps GiveFeedback on every mouse move.
            void OnFeedback(object s, GiveFeedbackEventArgs args) => preview.MoveToCursor();
            BtnDragOut.GiveFeedback += OnFeedback;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragDrop.DoDragDrop(BtnDragOut, data, DragDropEffects.Copy);
            }
            finally
            {
                BtnDragOut.GiveFeedback -= OnFeedback;
                preview.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Editor drag-out failed", ex);
        }
    }

    /// <summary>Writes <paramref name="flat"/> off-thread into the temp folder, then disposes it.</summary>
    private async Task<string> WriteDragFileAsync(SD.Bitmap flat)
    {
        string dir = TempFileJanitor.WinShotTempDirectory;
        string path = FileNamer.NextUniquePath(_settings, dir, "png");
        await Task.Run(() =>
        {
            using (flat)
            {
                System.IO.Directory.CreateDirectory(dir);
                TempFileJanitor.DeleteOldFiles(dir, DateTimeOffset.UtcNow, TimeSpan.FromDays(1), maxFilesToDelete: 50);
                ImageSaver.Save(flat, path);
            }
        });
        return path;
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        try
        {
            var flat = Flatten();
            await CaptureService.CopyToClipboardAsync(flat, takeOwnership: true);
            if (!IsVisible) return;
            // BtnCopy is a round glyph button — flash a checkmark, then restore the copy glyph.
            BtnCopy.Content = "";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) => { timer.Stop(); BtnCopy.Content = ""; };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Editor copy failed", ex);
        }
    }

    /// <summary>Bottom-bar zoom preset dropdown: Fit / 50% / 100% / 150% / 200%.</summary>
    private void OnZoomPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is not ComboBoxItem item) return;
        string choice = (item.Content as string) ?? "Fit";
        if (choice == "Fit")
        {
            FitToView();
            return;
        }
        if (choice == "Fit Width")
        {
            FitToWidth();
            return;
        }
        if (double.TryParse(choice.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out double pct)
            && Viewport.ActualWidth > 1 && Viewport.ActualHeight > 1)
        {
            ZoomAt(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), pct / 100.0);
        }
    }

    /// <summary>Pin the flattened result to the screen as a floating always-on-top window.</summary>
    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        try
        {
            // FastPinWindow takes ownership of the bitmap and disposes it on close.
            var pin = new WinShot.Pin.FastPinWindow(Flatten(), _settings);
            PerfLog.TrackFirstShown(pin, "editor pin window");
            pin.Show();
        }
        catch (Exception ex)
        {
            Log.Error("Editor pin failed", ex);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
        try
        {
            System.IO.Directory.CreateDirectory(_settings.Current.SaveFolder);
            SaveFileDialog dialog;
            if (_projectPath is string proj)
            {
                // A session opened from (or saved as) a project defaults back to that file.
                dialog = new SaveFileDialog
                {
                    FileName = System.IO.Path.GetFileName(proj),
                    InitialDirectory = System.IO.Path.GetDirectoryName(proj) is { Length: > 0 } dir
                        ? dir : _settings.Current.SaveFolder,
                    Filter = SaveDialogFilter,
                    FilterIndex = 4,
                };
            }
            else
            {
                dialog = new SaveFileDialog
                {
                    FileName = FileNamer.Next(_settings, _settings.Current.ImageFormat),
                    InitialDirectory = _settings.Current.SaveFolder,
                    Filter = SaveDialogFilter,
                    FilterIndex = _settings.Current.ImageFormat switch
                    {
                        "jpg" => 2,
                        "webp" => 3,
                        _ => 1,
                    },
                };
            }
            if (dialog.ShowDialog(this) != true) return;

            if (string.Equals(System.IO.Path.GetExtension(dialog.FileName), ".winshot",
                    StringComparison.OrdinalIgnoreCase))
            {
                await SaveProjectAsync(dialog.FileName);
                return;
            }

            var flat = Flatten();
            await Task.Run(() =>
            {
                using (flat)
                {
                    ImageSaver.Save(flat, dialog.FileName);
                    _history.Add(flat);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Editor save failed", ex);
        }
    }

    // ------------------------------------------------------ project (.winshot)

    /// <summary>
    /// Writes the current session to a .winshot project: a ZIP with the source bitmap
    /// (including any baked blur/pixelate/crop), annotations.json describing every
    /// live annotation, and the embedded bitmaps of image annotations. FileMode.Create
    /// inside the serializer means re-saving overwrites the previous file cleanly.
    /// </summary>
    private async Task SaveProjectAsync(string path)
    {
        ProjectSnapshot snapshot = CreateProjectSnapshot();
        try
        {
            await Task.Run(() =>
            {
                using (snapshot.Source)
                    ProjectSerializer.Save(path, snapshot.Source, snapshot.Document, snapshot.Images);
            });
        }
        catch
        {
            snapshot.Source.Dispose();
            throw;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _projectPath = path;
            Title = $"WinShot Editor — {System.IO.Path.GetFileName(path)}";
        });
    }

    private ProjectSnapshot CreateProjectSnapshot()
    {
        CommitText();
        CommitPendingCurve();

        var doc = new ProjectDocument();
        var images = new List<BitmapSource>();
        int z = 0;
        foreach (UIElement el in AnnotationCanvas.Children)
        {
            if (el is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
                continue; // transient editor visuals are not part of the project
            var data = meta.Clone();
            data.Z = z++;
            if (el.RenderTransform is TranslateTransform t)
            {
                data.Tx = t.X;
                data.Ty = t.Y;
            }
            else
            {
                data.Tx = 0;
                data.Ty = 0;
            }
            if (data.Type == AnnotationData.TypeImage && el is Image img && img.Source is BitmapSource bs)
            {
                data.ImageIndex = images.Count;
                if (bs.CanFreeze && !bs.IsFrozen)
                    bs.Freeze();
                images.Add(bs);
                data.Rect = new[] { Canvas.GetLeft(img), Canvas.GetTop(img), img.Width, img.Height };
            }
            doc.Annotations.Add(data);
        }

        return new ProjectSnapshot((SD.Bitmap)_source.Clone(), doc, images);
    }

    private sealed record ProjectSnapshot(
        SD.Bitmap Source,
        ProjectDocument Document,
        IReadOnlyList<BitmapSource> Images);

    /// <summary>
    /// Reopens a .winshot project file and reconstructs the editing session: the
    /// source bitmap plus every annotation as a live, editable canvas element.
    /// Returns null (after Log.Error) when the file cannot be parsed.
    /// </summary>
    public static EditorWindow? OpenProject(string path, SettingsService settings, HistoryService history)
    {
        SD.Bitmap? source = null;
        try
        {
            var (bitmap, doc, images) = ProjectSerializer.Load(path);
            source = bitmap;

            // Build (and validate) every element before constructing the window so a
            // malformed entry can never leak a half-initialized editor.
            var built = doc.Annotations
                .OrderBy(a => a.Z)
                .Select(a => (Data: a, Element: ProjectSerializer.CreateElement(a, images)))
                .ToList();

            var win = new EditorWindow(bitmap, settings, history);
            source = null; // the window owns the bitmap now (disposed on close)
            foreach (var (data, element) in built)
            {
                if (element is FrameworkElement fe) fe.Tag = data;
                if (data.Tx != 0 || data.Ty != 0)
                    element.RenderTransform = new TranslateTransform(data.Tx, data.Ty);
                win.AnnotationCanvas.Children.Add(element);
                if (data.Type == AnnotationData.TypeStep && data.Number is int n && n >= win._nextStep)
                    win._nextStep = n + 1;
            }
            win._projectPath = path;
            win.Title = $"WinShot Editor — {System.IO.Path.GetFileName(path)}";
            return win;
        }
        catch (Exception ex)
        {
            source?.Dispose();
            Log.Error($"Failed to open WinShot project: {path}", ex);
            return null;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
