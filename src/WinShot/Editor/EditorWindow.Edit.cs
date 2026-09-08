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
using WinShot.Recording;
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

    private void ClearCropPreview(bool restoreTool = false)
    {
        _pendingCrop = null;
        _adjustingCrop = false;
        _activeHandle = -1;
        if (_handleKind == HandleKind.Crop) _handleKind = HandleKind.None;
        HideDragRect();
        HideHandles();
        CropPanel.Visibility = Visibility.Collapsed;
        if (restoreTool && _tool == EditorTool.Crop)
        {
            _tool = _toolBeforeCrop == EditorTool.Crop ? EditorTool.Select : _toolBeforeCrop;
            UpdateCursor();
            UpdateContextPanels();
            UpdateGroupButtonChrome();
        }
    }

    // ------------------------------------------------------------- undo/redo

    private void Push(EditorAction action, bool apply = true)
    {
        if (apply) action.Redo();
        PushUndo(action);
        DiscardRedoStack();
        // Survey re-derives counter numbers after EVERY mutation, not just creation
        // (PDFViewer.jsx:24220/24240, annotationEraseCommitPlan.js:173).
        RenumberCounters();
        UpdateUndoRedoButtons();
    }

    /// <summary>
    /// Appends to the undo history, dropping the oldest entries past Survey's 100-deep cap
    /// (PDFViewer.jsx:23652). Discard() lets a trimmed blur/pixelate step release its backup
    /// bitmap, which would otherwise be held for the life of the window.
    /// </summary>
    private void PushUndo(EditorAction action)
    {
        _undoStack.Add(action);
        while (_undoStack.Count > UndoDepthLimit)
        {
            _undoStack[0].Discard();
            _undoStack.RemoveAt(0);
        }
    }

    private EditorAction PopUndo()
    {
        var action = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        return action;
    }

    /// <summary>Clears the redo stack, letting each dropped action release any
    /// resources it can no longer replay (e.g. blur/pixelate backup bitmaps).</summary>
    private void DiscardRedoStack()
    {
        while (_redoStack.Count > 0)
            _redoStack.Pop().Discard();
    }

    /// <summary>
    /// Records a newly drawn annotation. <paramref name="select"/> is false for freehand
    /// ink: Survey deliberately leaves pen and highlighter strokes unselected so continuous
    /// drawing is not interrupted (PageAnnotationLayer.jsx:5246-5247).
    /// </summary>
    private void PushAddElement(UIElement element, bool select = true)
    {
        Push(new EditorAction(
            undo: () => AnnotationCanvas.Children.Remove(element),
            redo: () =>
            {
                if (!AnnotationCanvas.Children.Contains(element))
                    AnnotationCanvas.Children.Add(element);
            }), apply: false);
        if (select) Select(element);
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
        var action = PopUndo();
        await action.UndoAsync();
        RenumberCounters();
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
        RenumberCounters();
        PushUndo(action);
        UpdateUndoRedoButtons();
    }

    private void UpdateUndoRedoButtons()
    {
        BtnUndo.IsEnabled = _undoStack.Count > 0;
        BtnRedo.IsEnabled = _redoStack.Count > 0;
        // Every undoable mutation funnels through here (Push / UndoAsync / RedoAsync), so
        // this is where a highlighter's backdrop can have changed underneath it.
        RefreshHighlighters();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Survey listens for the counter drag's Shift / Escape on the WINDOW in the capture
        // phase, ahead of every in-page handler, and only while a drag is live
        // (PDFViewer.jsx:26202-26257). Escape cancels the placement outright; Shift freezes
        // the nub tip so the badge orbits it.
        if (_counterDrag is not null)
        {
            if (e.Key == Key.Escape) { CancelCounterDrag(); e.Handled = true; return; }
            if (e.Key is Key.LeftShift or Key.RightShift) { FreezeCounterTip(); e.Handled = true; return; }
        }
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
                case EditorShortcutCommand.FitAndCenter: FitToView(); PersistZoomPreference(); break;
                case EditorShortcutCommand.FitWidth: FitToWidth(); PersistZoomPreference(); break;
                case EditorShortcutCommand.FitHeight: FitToHeight(); PersistZoomPreference(); break;
                case EditorShortcutCommand.ZoomIn: ZoomAt(ViewportCentre(), _zoom * ZoomStepFactor); break;
                case EditorShortcutCommand.ZoomOut: ZoomAt(ViewportCentre(), _zoom / ZoomStepFactor); break;
                case EditorShortcutCommand.Copy: OnCopy(this, new RoutedEventArgs()); break;
                case EditorShortcutCommand.Paste: PasteAnnotations(); break;
                case EditorShortcutCommand.Duplicate: DuplicateSelectedAnnotations(); break;
                case EditorShortcutCommand.ActualSize: ZoomAt(ViewportCentre(), 1); PersistZoomPreference(); break;
                case EditorShortcutCommand.SaveAs: OnSave(this, new RoutedEventArgs()); break;
            }
            e.Handled = true;
            return;
        }

        // Preserve the existing rule that modified letters never activate drawing tools.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;

        // A selected counter takes typed digits as its new number; anything else ends the run
        // and commits it as one undo entry.
        if (TryRetypeStepNumber(e.Key)) { e.Handled = true; return; }
        CommitStepRetype();

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
        // Survey's only modified tool letter is Shift+E (eraser, forced to partial); every
        // other letter still requires no modifiers (PDFViewer.jsx:23348-23373).
        if (onlyShiftOrNone && _pendingCurve is null &&
            TrySelectToolByKey(e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
        {
            e.Handled = true;
            return;
        }

        // Survey treats Backspace as an exact synonym for Delete (SVGAnnotationLayer.jsx:839).
        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selected is not null) { DeleteSelected(); e.Handled = true; }
        }
        else if (e.Key == Key.Escape)
        {
            // Esc leaves Survey's bbox edit mode first, before any other Escape meaning.
            if (_bboxEditTarget is not null && !_movingSelection && _activeHandle < 0)
            {
                LeaveBboxEditMode(null);
                UpdateSelectionVisual();
                e.Handled = true;
            }
            else if (_movingSelection)
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
        if (_counterDrag is not null && e.Key is Key.LeftShift or Key.RightShift)
        {
            // Releasing Shift hands the body back to the cursor with the offset it had at
            // release, so slide resumes with no jump.
            ReleaseCounterTip();
            e.Handled = true;
            return;
        }
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
        return HighlighterBlend.Flatten(CanvasHost, BaseTiles, AnnotationCanvas, _source.Width, _source.Height);
    }

    // ------------------------------------------------- drag-out ("Drag & Drop")

    private Point _dragOutStart;
    private bool _dragOutArmed;
    private BitmapSource? _dragThumbnail;
    private Task<string>? _dragFileTask;
    private DragPreview? _dragPreview;

    /// <summary>
    /// Flattening and PNG-encoding on mouse-down, not on the drag threshold, is what keeps the
    /// drag from stalling: by the time the pointer has moved far enough to start the drag, the
    /// file is already written and DoDragDrop can begin on the very next message.
    /// </summary>
    private void OnDragOutMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragOutArmed = true;
        _dragOutStart = e.GetPosition(this);
        if (_sourceOperationActive) return;

        try
        {
            var flat = Flatten();
            _dragThumbnail = DragPreview.CreateThumbnail(flat, VisualTreeHelper.GetDpi(this).DpiScaleX);
            _dragFileTask = WriteDragFileAsync(flat);
            _dragPreview ??= new DragPreview();
        }
        catch (Exception ex)
        {
            _dragThumbnail = null;
            _dragFileTask = null;
            Log.Error("Editor drag-out prewarm failed", ex);
        }
    }

    /// <summary>
    /// Press-and-drag the bottom-bar pill to drop the flattened image into another app as a real
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
        if (_dragFileTask is not Task<string> fileTask || _dragThumbnail is not BitmapSource thumbnail)
            return;

        var preview = _dragPreview;
        try
        {
            preview?.Show(thumbnail, VisualTreeHelper.GetDpi(this).DpiScaleX);

            // Normally already finished — the encode started back on mouse-down.
            string path = await fileTask;
            if (!IsVisible) return;

            // OLE pumps GiveFeedback on every mouse move, so the preview tracks the cursor 1:1.
            void OnFeedback(object s, GiveFeedbackEventArgs args) => preview?.MoveToCursor();
            BtnDragOut.GiveFeedback += OnFeedback;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragDrop.DoDragDrop(BtnDragOut, data, DragDropEffects.Copy);
            }
            finally
            {
                BtnDragOut.GiveFeedback -= OnFeedback;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Editor drag-out failed", ex);
        }
        finally
        {
            preview?.Hide();
            _dragThumbnail = null;
            _dragFileTask = null;
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
            ThemedMessageDialog.Show(this, "Couldn't copy",
                "WinShot could not copy the edited image. Try again or use Save as…");
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
            Vector savedOffset = AnnotationTransform.OffsetOf(el);
            data.Tx = savedOffset.X;
            data.Ty = savedOffset.Y;
            data.Angle = AnnotationTransform.AngleOf(el);
            if (data.Type is AnnotationData.TypeRectangle or AnnotationData.TypeEllipse)
            {
                double l = Canvas.GetLeft(fe); if (double.IsNaN(l)) l = 0;
                double t0 = Canvas.GetTop(fe); if (double.IsNaN(t0)) t0 = 0;
                data.Rect = new[] { l, t0, Math.Max(1, fe.Width), Math.Max(1, fe.Height) };
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
                // Persisted bold/italic/underline/strike/align have to reach the callout's
                // TextBlock; nothing else on the load path applies them.
                if (element is CalloutAnnotation loadedCallout) ApplyTextFormat(loadedCallout, data);
                if (element is FrameworkElement target && (data.Tx != 0 || data.Ty != 0 || data.Angle != 0))
                    AnnotationTransform.Apply(target, new Vector(data.Tx, data.Ty), data.Angle);
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

    /// <summary>Copy the flattened image, then close — screenshot-editor Done.</summary>
    private async void OnDone(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive)
        {
            ThemedMessageDialog.Show(this, "Still working",
                "Wait for the image to finish loading, then click Done.");
            return;
        }
        try
        {
            var flat = Flatten();
            await CaptureService.CopyToClipboardAsync(flat, takeOwnership: true);
        }
        catch (Exception ex)
        {
            Log.Error("Editor done-copy failed", ex);
            ThemedMessageDialog.Show(this, "Couldn't copy",
                "WinShot could not copy the edited image. The editor will stay open so you can save or try again.");
            return;
        }
        Close();
    }

    /// <summary>
    /// The composed image, handed over once as the editor closes. The quick-actions card
    /// the session started from listens so it can update in place instead of being left
    /// holding the unannotated capture. The handler owns the bitmap it is given.
    /// </summary>
    internal event Action<SD.Bitmap>? ImageCommitted;

    /// <summary>
    /// Raised from the close path rather than from the save dialog. "Done" is the editor's
    /// primary exit and Copy never touches disk at all, so raising this only after a
    /// successful "Save as…" left the card holding the pre-annotation pixels on every flow
    /// people actually use — and every card verb (Copy, Save, drag-out, Pin, Extract text)
    /// then served the stale bitmap.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closed || ImageCommitted is not { } committed)
            return;
        // Nothing was drawn, so nothing to hand back. Flatten() is a full-size WPF render
        // and the card answers it by re-encoding the on-disk history file — opening the
        // editor and pressing Done used to cost both, and a JPEG history entry a
        // generation of quality, for a visit that changed nothing.
        if (_undoStack.Count == 0)
            return;
        // The same guard every other Flatten() call site takes. Flatten reads _source for
        // the render size, and a crop, resize or background op swaps _source on an async
        // continuation — closing the editor while one is in flight would hand the card a
        // canvas rendered at the pre-swap dimensions.
        if (_sourceOperationActive)
            return;

        // Cleared first: a handler that reopens or re-closes must not be handed a second copy.
        ImageCommitted = null;
        try
        {
            committed(Flatten());
        }
        catch (Exception ex)
        {
            // The card fades back in holding the capture as it was before the editor opened,
            // so without this the user draws, presses Done, and gets the original back with
            // nothing on screen saying the annotations did not make it — and Copy, Save,
            // drag-out and Pin then all serve the un-annotated bitmap.
            Log.Error("Handing the composed image back to the quick-actions card failed", ex);
            App.Toast(
                // What actually failed is the hand-off, not the save: this runs on the close
                // path whatever the user did in the editor, so after a successful "Save a
                // copy…" the annotated file is on disk and only the card missed it. Same
                // wording the card raises for its own half of this failure.
                "Your annotations didn't reach the card",
                FriendlyError.For(ex),
                severity: ToastSeverity.Error);
        }
    }


    /// <summary>
    /// Two frames: the picture WITHOUT the added images, then with them. Aligning an
    /// "after" screenshot over a "before" at half opacity is how the difference gets found;
    /// this is how it gets shown to somebody else.
    /// </summary>
    private async void OnExportGif(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive || _gifExportRunning) return;
        CommitText();
        CommitPendingCurve();

        var overlays = ImageAnnotations();
        if (overlays.Count == 0)
        {
            // The button is hidden without one, so this is belt and braces rather than the
            // path anyone takes.
            App.Toast("Export GIF", "Add an image to the canvas first.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = System.IO.Path.ChangeExtension(FileNamer.Next(_settings, "gif"), ".gif"),
            InitialDirectory = _settings.Current.SaveFolder,
            Filter = "GIF animation|*.gif",
        };
        if (dialog.ShowDialog(this) != true) return;

        SD.Bitmap? before = null;
        SD.Bitmap? after = null;
        // Two flattens plus an encode is seconds on a large canvas. Without this the button
        // stays live and says nothing is happening, and a second click opens a second save
        // dialog over the first — the same guard OCR takes for the same reason.
        _gifExportRunning = true;
        ExportGifBtn.IsEnabled = false;
        // Both flattens run on the UI thread before the encode moves off it, so the window
        // is genuinely busy for that stretch — say so the way every other long edit does.
        Cursor = Cursors.Wait;
        try
        {
            // "Before" is the canvas with the added images hidden — not the raw source:
            // arrows and labels drawn on top belong in BOTH frames, or the animation is a
            // flip between two different pictures rather than a comparison.
            var restore = new List<(UIElement Element, Visibility Was)>();
            foreach (UIElement overlay in overlays)
            {
                restore.Add((overlay, overlay.Visibility));
                overlay.Visibility = Visibility.Hidden;
            }
            try
            {
                before = Flatten();
            }
            finally
            {
                foreach (var (element, was) in restore)
                    element.Visibility = was;
            }
            after = Flatten();

            SD.Bitmap frame1 = before, frame2 = after;
            await Task.Run(() =>
            {
                using var stream = System.IO.File.Create(dialog.FileName);
                // Two frames at 1 fps: a full second on each side is what makes a
                // difference readable. Faster reads as a flicker, not a comparison.
                using var gif = new GifEncoder(stream, frame1.Width, frame1.Height, fps: 1);
                gif.AddFrame(frame1);
                gif.AddFrame(frame2);
                gif.Finish();
            });

            App.Toast("Saved the GIF", System.IO.Path.GetFileName(dialog.FileName),
                severity: ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error($"Before/after GIF export failed: {dialog.FileName}", ex);
            // The encode can outlive the window; ThemedMessageDialog assigns Owner
            // unconditionally and WPF throws on a closed one, out of an async void handler.
            if (!_closed)
                ThemedMessageDialog.Show(this, "Save failed", FriendlyError.For(ex));
        }
        finally
        {
            _gifExportRunning = false;
            if (!_closed) { ExportGifBtn.IsEnabled = true; UpdateCursor(); }
            before?.Dispose();
            after?.Dispose();
        }
    }


    /// <summary>A GIF export is a full re-render twice over; one at a time.</summary>
    private bool _gifExportRunning;

    /// <summary>The image annotations on the canvas, bottom to top.</summary>
    private List<UIElement> ImageAnnotations()
    {
        var found = new List<UIElement>();
        foreach (UIElement child in AnnotationCanvas.Children)
            if (child is FrameworkElement fe && fe.Tag is AnnotationData meta &&
                meta.Type == AnnotationData.TypeImage)
                found.Add(child);
        return found;
    }

}
