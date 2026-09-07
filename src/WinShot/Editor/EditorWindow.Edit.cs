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

    /// <summary>Keeps the crop bar's Image size button reading the real dimensions.</summary>
    private void SyncImageSizeLabel()
    {
        if (ImageSizeLabel is null) return;
        ImageSizeLabel.Text = string.Create(CultureInfo.InvariantCulture,
            $"Image size: {_source.Width} × {_source.Height}");
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
            !ThemedMessageDialog.Confirm(this, "Flatten annotations?",
                "Your annotations will be baked into the image before this operation. " +
                "They will no longer be editable as separate objects.",
                "Flatten"))
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
        CropGuides.Visibility = Visibility.Collapsed;
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
        // An image arriving or leaving is always an action, so this is the one place that
        // sees every moment the before/after GIF becomes possible or impossible.
        SyncExportGifButton();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Let an open text annotation keep its own keyboard behavior.
        if (Keyboard.FocusedElement is TextBox) return;

        // An open popover owns Escape: it is the only way out of a menu for a keyboard
        // user, and it must not also deselect or abort whatever is on the canvas behind it.
        if (e.Key == Key.Escape && CloseOpenPopover())
        {
            e.Handled = true;
            return;
        }

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
        // Z arms the zoom gesture for as long as it is held: click a point or drag a
        // rectangle. It is a held modifier rather than a tool, so it never takes the tool
        // you were using away from you.
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (!_zoomKeyDown)
            {
                _zoomKeyDown = true;
                UpdateCursor();
            }
            e.Handled = true;
            return;
        }
        // Q and W: 100 % on the two ends of whatever is selected.
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Q or Key.W)
        {
            ZoomToCorner(bottomRight: e.Key == Key.W);
            e.Handled = true;
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
                case EditorShortcutCommand.ActualSize: ZoomToPercent(1.0); break;
                case EditorShortcutCommand.ZoomIn: StepZoom(1.2); break;
                case EditorShortcutCommand.ZoomOut: StepZoom(1 / 1.2); break;
                // Marks first: Ctrl+C with something selected copies the MARKS, and only
                // falls through to the picture when nothing is selected.
                case EditorShortcutCommand.Copy:
                    if (!CopySelectedAnnotations()) OnCopy(this, new RoutedEventArgs());
                    break;
                case EditorShortcutCommand.Paste: PasteAnnotations(); break;
                case EditorShortcutCommand.Duplicate: DuplicateSelectedAnnotations(); break;
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

        // Typing over a selected counter re-numbers it, before the letter keys get a look
        // in — a digit is never a tool shortcut, so nothing is shadowed.
        if (Keyboard.Modifiers == ModifierKeys.None && TryRetypeStepNumber(e.Key))
        {
            e.Handled = true;
            return;
        }
        CommitStepRetype(); // any other key ends the run

        // Single-letter tool shortcuts (no modifiers). Ignored while a curved arrow is
        // mid-edit so Escape/commit stays the only way out of that transient mode.
        if (Keyboard.Modifiers == ModifierKeys.None && _pendingCurve is null && TrySelectToolByKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            if (_selected is not null || _multiSelected.Count > 1) { DeleteSelected(); e.Handled = true; }
        }
        else if (e.Key == Key.Escape)
        {
            if (_zoomKeyDown || _zoomDragging)
            {
                AbortZoomGesture();
                _zoomKeyDown = false;
                _zoomArmedByMenu = false;
                UpdateCursor();
                e.Handled = true;
            }
            else if (_marqueeActive)
            {
                AbortMarquee();
                Viewport.ReleaseMouseCapture();
                e.Handled = true;
            }
            else if (_movingSelection)
            {
                AbortMove();
                Viewport.ReleaseMouseCapture();
                e.Handled = true;
            }
            else if (_multiSelected.Count > 1)
            {
                ClearMultiSelection();
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
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.Z && _zoomKeyDown)
        {
            _zoomKeyDown = false;
            // Letting Z go mid-drag abandons the gesture rather than zooming to a
            // rectangle the user was no longer choosing.
            AbortZoomGesture();
            UpdateCursor();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Space) return;
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
            // A pending crop rectangle means "copy this bit", not "copy the picture" — and
            // it does NOT close the window, so the crop is still there to adjust afterwards.
            SD.Bitmap flat = Flatten();
            if (_pendingCrop is SD.Rectangle region && region.Width > 1 && region.Height > 1)
            {
                try
                {
                    SD.Bitmap cropped = CaptureService.Crop(flat, region);
                    flat.Dispose();
                    flat = cropped;
                }
                catch (ArgumentException)
                {
                    // Out-of-range crop: fall back to the whole image rather than nothing.
                }
            }
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
            // Without this the glyph simply never becomes a checkmark and the user pastes
            // whatever was on the clipboard before.
            ThemedMessageDialog.Show(this, "Copy failed", "WinShot could not put this image on the clipboard. Another app may be holding it — try again in a moment.");
        }
    }

    /// <summary>
    /// The zoom menu. Every row carries its shortcut in the gesture column, which is how
    /// the editor advertises the keys it now binds instead of hiding them.
    /// </summary>
    private void OnZoomMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ZoomMenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            // Without both styles this pops the Aero2 default — white chrome over the dark
            // editor, on the control built to replace the dead zoom combo.
            Style = (Style)FindResource("DarkContextMenu"),
        };
        var itemStyle = (Style)FindResource("DarkMenuItem");
        void Row(string header, string gesture, Action run)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, Style = itemStyle };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }

        Row("Fit", "Ctrl+1", FitToView);
        Row("Fit width", "", FitToWidth);
        menu.Items.Add(new Separator());
        Row("50%", "", () => ZoomToPercent(0.5));
        Row("100%", "Ctrl+0", () => ZoomToPercent(1.0));
        Row("150%", "", () => ZoomToPercent(1.5));
        Row("200%", "", () => ZoomToPercent(2.0));
        menu.Items.Add(new Separator());
        Row("Zoom in", "Ctrl++", () => StepZoom(1.2));
        Row("Zoom out", "Ctrl+-", () => StepZoom(1 / 1.2));
        menu.Items.Add(new Separator());
        Row("Zoom to top left", "Q", () => ZoomToCorner(bottomRight: false));
        Row("Zoom to bottom right", "W", () => ZoomToCorner(bottomRight: true));
        Row("Zoom to a region", "Z + drag", ArmZoomGesture);

        menu.IsOpen = true;
    }

    // ------------------------------------------------- before/after GIF export

    /// <summary>
    /// Two frames: the picture WITHOUT the added images, then with them. Aligning an
    /// "after" screenshot over a "before" at half opacity is how the difference gets found;
    /// this is how it gets shown to somebody else.
    /// </summary>
    private async void OnExportGif(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationActive) return;
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
                ThemedMessageDialog.Show(this, "Couldn't save the GIF", FriendlyError.For(ex));
        }
        finally
        {
            before?.Dispose();
            after?.Dispose();
        }
    }

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

    /// <summary>
    /// Shows the GIF button once there is a second picture to flip to, and takes it away
    /// again when the last one goes. A one-frame before/after animation is a PNG.
    /// </summary>
    private void SyncExportGifButton()
    {
        if (ExportGifBtn is null) return;
        ExportGifBtn.Visibility = ImageAnnotations().Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
            ThemedMessageDialog.Show(this, "Pin failed", "WinShot could not pin this image to the screen.");
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
            ThemedMessageDialog.Show(this, "Save failed", ex is UnauthorizedAccessException or System.IO.IOException
                ? "WinShot could not write to that folder. Check the folder still exists and you have permission to write to it."
                : "WinShot could not save this image.");
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
            Vector offset = AnnotationTransform.OffsetOf(el);
            data.Tx = offset.X;
            data.Ty = offset.Y;
            data.Angle = AnnotationTransform.AngleOf(el);
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
                .Select(a => (Data: a, Element: ProjectSerializer.CreateElement(a, images, source)))
                .ToList();

            var win = new EditorWindow(bitmap, settings, history);
            source = null; // the window owns the bitmap now (disposed on close)
            foreach (var (data, element) in built)
            {
                if (element is FrameworkElement fe) fe.Tag = data;
                if (element is FrameworkElement target && (data.Tx != 0 || data.Ty != 0 || data.Angle != 0))
                    AnnotationTransform.Apply(target, new Vector(data.Tx, data.Ty), data.Angle);
                win.AnnotationCanvas.Children.Add(element);
                if (data.Type == AnnotationData.TypeStep && data.Number is int n && n >= win._nextStep)
                    win._nextStep = n + 1;
            }
            win._projectPath = path;
            win.Title = $"WinShot Editor — {System.IO.Path.GetFileName(path)}";
            // CreateElement painted each redaction at its ORIGINAL rect; the move offsets
            // were applied above, so re-read the pixels each one actually covers now.
            win.RefreshAllRedactions();

            // A rotation pivot is the element's own centre, which is only known once the
            // element has been measured. Re-seat the pivots after the first layout pass,
            // otherwise every restored rotation would spin about (0,0).
            if (built.Any(b => b.Data.Angle != 0))
            {
                win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    foreach (var (data, element) in built)
                    {
                        if (data.Angle != 0 && element is FrameworkElement fe)
                            AnnotationTransform.SetAngle(fe, data.Angle);
                    }
                }));
            }

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
