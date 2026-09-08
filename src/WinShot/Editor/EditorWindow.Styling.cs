using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Editor;

public partial class EditorWindow : Window
{
    // ----------------------------------------------------- live restyle (Gap 1)

    /// <summary>
    /// Re-styles the currently selected annotation in place when the user changes color,
    /// thickness, or fill, and records one undoable <see cref="EditorAction"/>. Only the
    /// non-null arguments are applied; the rest keep the element's current look. Captures a
    /// before/after snapshot of the affected visual properties and the stored
    /// <see cref="AnnotationData"/> so undo/redo simply replay the appropriate snapshot.
    /// </summary>
    private void RestyleSelected(Color? color, double? thickness, ShapeFillMode? fill, double? opacity = null)
    {
        if (_selected is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
            return;
        // Color is irrelevant to a few annotation kinds (spotlight/emoji/image); thickness
        // and fill only make sense for the shapes that carry them. Skip no-op restyles so
        // those annotations don't push empty undo entries.
        if (!CanRestyle(_selected, color, thickness, fill) &&
            !(opacity is not null && CanSetOpacity(_selected)))
            return;

        // A color pick (swatch / eyedropper) is opaque, but the annotation may carry a
        // reduced alpha — from the highlighter's translucent base or an earlier opacity
        // change. Preserve that existing alpha so changing the hue never silently resets
        // opacity. An explicit opacity change (opacity != null) overrides this below.
        if (color is Color picked && opacity is null)
        {
            byte fallback = meta.Type == AnnotationData.TypeHighlighter ? (byte)0x59 : (byte)0xFF;
            byte alpha = meta.Color is string hex0 && TryParseColor(hex0, out var cur) ? cur.A : fallback;
            color = Color.FromArgb(alpha, picked.R, picked.G, picked.B);
        }

        var before = SnapshotStyle(_selected, meta);
        var after = before.With(color, thickness, fill, opacity);
        ApplyStyleSnapshot(_selected, after);

        UIElement el = _selected;
        Push(new EditorAction(
            undo: () => ApplyStyleSnapshot(el, before),
            redo: () => ApplyStyleSnapshot(el, after)), apply: false);
        UpdateSelectionVisual();
    }

    /// <summary>
    /// The Fill tab: gives the selected shape its own fill colour, as one undoable step.
    /// No-op for anything without a fill channel.
    /// </summary>
    private void RestyleSelectedFillColor(Color fillColor)
    {
        if (_selected is not Shape shape || shape.Tag is not AnnotationData meta ||
            meta.Type is not (AnnotationData.TypeRectangle or AnnotationData.TypeEllipse))
            return;

        var before = meta.Clone();
        var after = meta.Clone();
        after.FillColor = ToHex(fillColor);
        if (before.FillColor == after.FillColor) return;

        void Apply(AnnotationData data)
        {
            shape.Tag = data;
            Color strokeColor = data.Color is string hex && TryParseColor(hex, out var c) ? c : Colors.White;
            ShapeFillMode mode = Enum.TryParse(data.Fill, out ShapeFillMode m) ? m : ShapeFillMode.None;
            shape.Fill = ShapeFillBrush.Create(mode, strokeColor,
                ProjectSerializer.ParseOptionalColor(data.FillColor));
        }

        Apply(after);
        Push(new EditorAction(undo: () => Apply(before), redo: () => Apply(after)), apply: false);
    }

    private static bool CanRestyle(UIElement element, Color? color, double? thickness, ShapeFillMode? fill)
    {
        if (element is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
            return false;
        return meta.Type switch
        {
            AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow or AnnotationData.TypeLine
                or AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter =>
                color is not null || thickness is not null,
            AnnotationData.TypeRectangle or AnnotationData.TypeEllipse => true,
            AnnotationData.TypeText => color is not null,
            AnnotationData.TypeStep => color is not null || thickness is not null,
            _ => false, // emoji / spotlight / image carry no editable stroke style
        };
    }

    /// <summary>Whether an opacity change has a visible effect on this annotation kind (the color-bearing ones).</summary>
    private static bool CanSetOpacity(UIElement element) =>
        element is FrameworkElement fe && fe.Tag is AnnotationData meta && meta.Type switch
        {
            AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow or AnnotationData.TypeLine
                or AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter
                or AnnotationData.TypeRectangle or AnnotationData.TypeEllipse
                or AnnotationData.TypeText or AnnotationData.TypeStep => true,
            // A half-transparent image over the shot is how two frames get aligned by
            // difference, which is the whole reason it can be dialled at all.
            AnnotationData.TypeImage => true,
            _ => false,
        };

    /// <summary>
    /// Immutable snapshot of every property a restyle can touch, plus a fresh
    /// <see cref="AnnotationData"/> for the element's Tag, so save round-trips stay correct.
    /// </summary>
    private sealed record StyleSnapshot(
        Color StrokeColor,
        double Thickness,
        ShapeFillMode Fill,
        AnnotationData Meta)
    {
        public StyleSnapshot With(Color? color, double? thickness, ShapeFillMode? fill, double? opacity = null)
        {
            Color c = color ?? StrokeColor;
            double t = thickness ?? Thickness;
            ShapeFillMode f = fill ?? Fill;

            // Opacity rewrites the color's alpha. Highlighter has a translucent base alpha
            // (~0x59) that opacity scales; every other kind treats RGB as fully opaque and
            // sets alpha straight from the multiplier so the result is idempotent.
            if (opacity is double op)
            {
                byte baseAlpha = Meta.Type == AnnotationData.TypeHighlighter ? (byte)0x59 : (byte)0xFF;
                byte alpha = (byte)Math.Clamp(Math.Round(baseAlpha * op), 0, 255);
                c = Color.FromArgb(alpha, c.R, c.G, c.B);
            }

            var meta = Meta.Clone();
            // An image has no colour to hide an alpha in, so its transparency is a field
            // of its own and lands on the element rather than on a brush.
            if (opacity is double imageOp && Meta.Type == AnnotationData.TypeImage)
                meta.Alpha = Math.Clamp(imageOp, 0, 1);
            meta.Color = ToHex(c);
            // Always carried, even when this call did not change it: the arrow rebuild
            // reads its weight back off the metadata, and a null there would reset the
            // stroke to whatever the visual happened to hold.
            meta.Thickness = t;
            if (fill is not null) meta.Fill = f.ToString();
            return new StyleSnapshot(c, t, f, meta);
        }
    }

    private static StyleSnapshot SnapshotStyle(UIElement element, AnnotationData meta)
    {
        Color stroke = meta.Color is string hex && TryParseColor(hex, out var c) ? c : Colors.White;
        double thickness = meta.Thickness ?? 4;
        ShapeFillMode fill = Enum.TryParse(meta.Fill, out ShapeFillMode f) ? f : ShapeFillMode.None;
        return new StyleSnapshot(stroke, thickness, fill, meta);
    }

    /// <summary>Applies a snapshot to the live element and refreshes its stored AnnotationData.</summary>
    private void ApplyStyleSnapshot(UIElement element, StyleSnapshot snap)
    {
        if (element is not FrameworkElement fe || fe.Tag is not AnnotationData meta)
            return;
        var stroke = new SolidColorBrush(snap.StrokeColor);

        switch (meta.Type)
        {
            case AnnotationData.TypeImage:
                fe.Opacity = Math.Clamp(snap.Meta.Alpha ?? 1, 0, 1);
                break;
            case AnnotationData.TypeArrow:
            case AnnotationData.TypeCurvedArrow:
                if (element is Path arrow)
                {
                    arrow.Stroke = stroke;
                    arrow.Fill = stroke;
                    arrow.StrokeThickness = snap.Thickness;
                    // The triangular head scales with thickness, so rebuild geometry —
                    // ApplyArrowStyleData also drops the stroke for the filled Fancy shape.
                    ApplyArrowStyleData(arrow, snap.Meta);
                }
                break;
            case AnnotationData.TypeLine:
            case AnnotationData.TypeFreehand:
            case AnnotationData.TypeHighlighter:
                if (element is Shape lineLike)
                {
                    // Highlighter keeps its baked-in translucent alpha; honor it from the snapshot.
                    lineLike.Stroke = stroke;
                    lineLike.StrokeThickness = snap.Thickness;
                }
                break;
            case AnnotationData.TypeRectangle:
            case AnnotationData.TypeEllipse:
                if (element is Shape boxLike)
                {
                    boxLike.Stroke = stroke;
                    boxLike.StrokeThickness = snap.Thickness;
                    boxLike.Fill = ShapeFillBrush.Create(snap.Fill, snap.StrokeColor,
                        ProjectSerializer.ParseOptionalColor(snap.Meta.FillColor));
                }
                break;
            case AnnotationData.TypeText:
                ApplyTextColor(element, stroke);
                break;
            case AnnotationData.TypeStep:
                if (element is Grid badge)
                {
                    bool lightFill = 0.299 * snap.StrokeColor.R + 0.587 * snap.StrokeColor.G +
                                     0.114 * snap.StrokeColor.B > 160;
                    var contrast = lightFill ? Colors.Black : Colors.White;
                    foreach (var child in badge.Children)
                    {
                        if (child is Ellipse ring)
                        {
                            ring.Fill = stroke;
                            ring.Stroke = new SolidColorBrush(contrast) { Opacity = 0.85 };
                        }
                        else if (child is TextBlock digit)
                        {
                            digit.Foreground = new SolidColorBrush(contrast);
                        }
                    }
                }
                break;
        }

        fe.Tag = snap.Meta;
    }

    /// <summary>Sets the foreground of a styled text label, walking into the Pill border's child.</summary>
    private static void ApplyTextColor(UIElement element, Brush foreground)
    {
        switch (element)
        {
            case TextBlock tb:
                tb.Foreground = foreground;
                break;
            case Path glyph: // Outline style
                glyph.Fill = foreground;
                break;
            case Border pill when pill.Child is TextBlock inner:
                inner.Foreground = foreground;
                break;
        }
    }

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseColor(string hex, out Color color)
    {
        if (ColorConverter.ConvertFromString(hex) is Color c)
        {
            color = c;
            return true;
        }
        color = Colors.White;
        return false;
    }

    // ----------------------------------------------------------- step mode (Gap 5)

    private void OnStepModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
            _stepLetters = string.Equals(tag, "Letter", StringComparison.Ordinal);
    }

    private void OnResetStepCounter(object sender, RoutedEventArgs e) => _nextStep = 1;

    // ------------------------------------------------------ glyph-button popovers

    /// <summary>
    /// One row of a style menu: the preview IS the choice (an arrow at its real weight, a
    /// text label in its own style), so no row needs a word to explain it. Shares
    /// SizePresetRow so every menu in the editor highlights and checks the same way.
    /// </summary>
    private RadioButton AddPopoverRow(Panel list, string group, object content, object tag, string automationName)
    {
        var row = new RadioButton
        {
            Style = (Style)FindResource("SizePresetRow"),
            GroupName = group,
            Tag = tag,
            Content = content,
        };
        AutomationProperties.SetName(row, automationName);
        list.Children.Add(row);
        return row;
    }

    private static void CheckRowByTag(Panel list, object tag)
    {
        foreach (RadioButton row in list.Children.OfType<RadioButton>())
            row.IsChecked = Equals(row.Tag, tag);
    }

    private void OnSelectModeClick(object sender, RoutedEventArgs e)
    {
        BuildSelectModeRows();
        CheckRowByTag(SelectModeList, _marqueePreference);
        OpenPopover(SelectModePopup, SelectModeList);
    }

    private void BuildSelectModeRows()
    {
        if (SelectModeList.Children.Count > 0) return;
        (MarqueeModePreference Mode, string Label)[] rows =
        {
            (MarqueeModePreference.Auto, "Auto — drag right encloses, left touches"),
            (MarqueeModePreference.AlwaysWindow, "Window — fully enclosed only"),
            (MarqueeModePreference.AlwaysCrossing, "Crossing — anything it touches"),
        };
        foreach ((MarqueeModePreference mode, string label) in rows)
        {
            var row = AddPopoverRow(SelectModeList, "SelectMode",
                new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis },
                mode, label);
            row.Click += (_, _) =>
            {
                _marqueePreference = mode;
                SelectModePopup.IsOpen = false;
            };
        }

        // Object snapping lives here rather than on the toolbar: it belongs to the pointer,
        // it is set once and forgotten, and Shottr — where the behaviour comes from — makes
        // it an explicit toggle rather than something that just happens to you.
        SelectModeList.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6),
            Fill = (Brush)FindResource("BorderBrush"),
        });
        var snap = new CheckBox
        {
            Style = (Style)FindResource("DarkCheckBox"),
            Content = "Snap to objects",
            IsChecked = _objectSnap,
            Margin = new Thickness(10, 2, 10, 6),
            ToolTip = "Line marks up with each other while dragging — hold Ctrl to override",
        };
        snap.SetValue(AutomationProperties.NameProperty, "Snap to objects");
        snap.SetValue(AutomationProperties.HelpTextProperty,
            "Line a dragged mark's edges and center up with the other marks. Hold Ctrl while dragging to override.");
        snap.Checked += (_, _) => _objectSnap = true;
        snap.Unchecked += (_, _) => _objectSnap = false;
        SelectModeList.Children.Add(snap);
    }

    private void OnArrowStyleClick(object sender, RoutedEventArgs e)
    {
        BuildArrowStyleRows();
        CheckRowByTag(ArrowStyleList, _tool == EditorTool.CurvedArrow ? ArrowStyle.Curved : _arrowStyle);
        OpenPopover(ArrowStylePopup, ArrowStyleList);
    }

    private void BuildArrowStyleRows()
    {
        if (ArrowStyleList.Children.Count > 0) return;
        foreach (ArrowStyle style in Enum.GetValues<ArrowStyle>())
        {
            var row = AddPopoverRow(ArrowStyleList, "ArrowStyle",
                BuildArrowPreview(style), style, ArrowStyleLabel(style));
            ArrowStyle picked = style;
            row.Click += (_, _) =>
            {
                ApplyArrowStyleChoice(picked);
                ArrowStylePopup.IsOpen = false;
            };
        }
    }

    /// <summary>
    /// What a screen reader says for an arrow row. The rows carry no text at all on screen —
    /// the preview IS the choice — so the accessible name is the only thing a non-sighted
    /// user gets, and "Double" on its own is not it.
    /// </summary>
    private static string ArrowStyleLabel(ArrowStyle style) => style switch
    {
        ArrowStyle.Double => "Double-headed arrow",
        ArrowStyle.Thin => "Thin arrow",
        ArrowStyle.Fancy => "Fancy arrow",
        ArrowStyle.Curved => "Curved arrow",
        _ => "Straight arrow",
    };

    /// <summary>A 40x16 preview drawn from the same geometry the tool will draw.</summary>
    private static Grid BuildArrowPreview(ArrowStyle style)
    {
        const double w = 40, h = 16, t = 3;
        var from = new Point(1, h / 2);
        var to = new Point(w - 1, h / 2);
        Geometry data = style == ArrowStyle.Curved
            ? AnnotationFactory.CurvedArrowGeometry(from, new Point(w / 2, 1), to, t)
            : AnnotationFactory.ArrowGeometry(from, to, t, style);

        var path = new Path
        {
            Data = data,
            Stroke = Brushes.White,
            Fill = Brushes.White,
            StrokeThickness = style == ArrowStyle.Fancy ? 0 : t,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = w,
            Height = h,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        path.SetBinding(Shape.StrokeProperty, ForegroundOfRow());
        path.SetBinding(Shape.FillProperty, ForegroundOfRow());

        var grid = new Grid();
        grid.Children.Add(path);
        return grid;
    }

    /// <summary>
    /// Binds a preview's ink to its own button's Foreground, so checked / hover / disabled
    /// states carry. ButtonBase, not RadioButton: the same previews are the content of the
    /// popover's rows AND of the toolbar button that opens it, and a binding that only looked
    /// for a RadioButton found no ancestor on the toolbar and painted nothing at all.
    /// </summary>
    private static System.Windows.Data.Binding ForegroundOfRow() =>
        new("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.FindAncestor,
                typeof(System.Windows.Controls.Primitives.ButtonBase), 1),
        };

    /// <summary>
    /// Picking an arrow style sets it for new arrows AND restyles the selection. Curved is
    /// the odd one: it is a different annotation type, so it arms the curved tool and
    /// converts a selected straight arrow rather than reshaping a head.
    /// </summary>
    private void ApplyArrowStyleChoice(ArrowStyle style)
    {
        if (style == ArrowStyle.Curved)
        {
            RestyleSelectedArrow(style);
            if (_selected is null && _tool != EditorTool.CurvedArrow)
                SetToolWithoutButton(EditorTool.CurvedArrow);
            return;
        }

        _arrowStyle = style;
        if (_tool == EditorTool.CurvedArrow)
            SetToolWithoutButton(EditorTool.Arrow);
        if (IsLoaded) RestyleSelectedArrow(style);
    }

    private void OnStrokeStyleClick(object sender, RoutedEventArgs e)
    {
        BuildStrokeDashRows();
        SyncStrokeStyleButtons();
        OpenPopover(StrokeStylePopup, StrokeDashList);
    }

    private void BuildStrokeDashRows()
    {
        if (StrokeDashList.Children.Count > 0) return;
        foreach (StrokeDash dash in Enum.GetValues<StrokeDash>())
        {
            var row = AddPopoverRow(StrokeDashList, "StrokeDash",
                BuildDashPreview(dash), dash, $"{dash} outline");
            StrokeDash picked = dash;
            row.Click += (_, _) =>
            {
                _strokeDash = picked;
                SyncStrokeStyleButtons();
                ApplyStrokeStyleToSelection();
                StrokeStylePopup.IsOpen = false;
            };
        }
    }

    /// <summary>
    /// A 40x16 preview drawn with the same DashArray and cap the tool will use, so the row
    /// IS the choice. Solid, dashed and dotted are three states; the glyph toggle that used
    /// to hold them could only cycle, and told dashed from dotted by tooltip alone.
    /// </summary>
    private static Grid BuildDashPreview(StrokeDash dash)
    {
        const double w = 40, h = 16, t = 3;
        var line = new System.Windows.Shapes.Line
        {
            X1 = 1,
            Y1 = h / 2,
            X2 = w - 1,
            Y2 = h / 2,
            StrokeThickness = t,
            StrokeDashArray = StrokeStyles.DashArray(dash),
            StrokeStartLineCap = StrokeStyles.CapFor(dash),
            StrokeEndLineCap = StrokeStyles.CapFor(dash),
            StrokeDashCap = StrokeStyles.CapFor(dash),
            Width = w,
            Height = h,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        line.SetBinding(Shape.StrokeProperty, ForegroundOfRow());

        var grid = new Grid();
        grid.Children.Add(line);
        return grid;
    }

    private void OnTextStyleClick(object sender, RoutedEventArgs e)
    {
        BuildTextStyleRows();
        CheckRowByTag(TextStyleList, _textStyle);
        OpenPopover(TextStylePopup, TextStyleList);
    }

    private void BuildTextStyleRows()
    {
        if (TextStyleList.Children.Count > 0) return;
        foreach (TextStyle style in Enum.GetValues<TextStyle>())
        {
            // Huge is the same look at double size; previewing it at double size would
            // blow the menu open, so every row previews at one readable size.
            // ponytail: the ink is the row's own resting foreground, not the row's live one.
            // The arrow preview binds to the row so hover and checked carry; this factory
            // paints three element kinds (TextBlock, Path, Border) from one Brush argument,
            // so following the row would mean teaching the factory about rows. A token, not
            // a literal white: TextPrimaryBrush is the same value over ElevatedBrush.
            FrameworkElement preview = AnnotationFactory.CreateStyledTextLabel(
                style.ToString(), (Brush)FindResource("TextPrimaryBrush"),
                (double)FindResource("TextBodyLg"), style);
            preview.VerticalAlignment = VerticalAlignment.Center;
            var row = AddPopoverRow(TextStyleList, "TextStyle", preview, style, $"{style} text");
            TextStyle picked = style;
            row.Click += (_, _) =>
            {
                _textStyle = picked;
                TextStylePopup.IsOpen = false;
            };
        }
    }

    private void OnCropRatioClick(object sender, RoutedEventArgs e)
    {
        BuildCropRatioRows();
        CheckRowByTag(CropRatioList, _cropRatioTag);
        OpenPopover(CropRatioPopup, CropRatioList);
    }

    private void BuildCropRatioRows()
    {
        if (CropRatioList.Children.Count > 0) return;
        foreach (string tag in new[] { "", "1:1", "4:3", "16:9", "9:16" })
        {
            string label = tag.Length == 0 ? "Free" : tag;
            var row = AddPopoverRow(CropRatioList, "CropRatio",
                new TextBlock { Text = label }, tag, label);
            string picked = tag;
            row.Click += (_, _) =>
            {
                ApplyCropRatio(picked);
                CropRatioPopup.IsOpen = false;
            };
        }
    }

    /// <summary>
    /// Re-shapes the selected arrow to a new <see cref="ArrowStyle"/>, recording one undoable
    /// action. Curved is a different annotation TYPE, not a different head, so picking it
    /// converts the mark (seeding the bend from <see cref="AnnotationFactory.DefaultCurveControl"/>)
    /// and picking a straight style converts it back. The endpoints stay put either way.
    /// </summary>
    private void RestyleSelectedArrow(ArrowStyle style)
    {
        if (_selected is not Path arrow || arrow.Tag is not AnnotationData meta ||
            meta.Type is not (AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow))
            return;

        bool wasCurved = meta.Type == AnnotationData.TypeCurvedArrow;
        bool wantCurved = style == ArrowStyle.Curved;
        var before = meta.Clone();
        if (wasCurved == wantCurved &&
            (wantCurved || AnnotationFactory.ParseArrowStyle(before.Style) == style))
            return; // nothing to do

        var after = meta.Clone();
        Point[] pts = (after.Points ?? Array.Empty<double[]>())
            .Where(p => p is { Length: >= 2 })
            .Select(p => new Point(p[0], p[1]))
            .ToArray();
        if (pts.Length < 2) return;
        Point from = pts[0], to = pts[^1];

        if (wantCurved)
        {
            Point control = pts.Length >= 3 ? pts[1] : AnnotationFactory.DefaultCurveControl(from, to);
            after.Type = AnnotationData.TypeCurvedArrow;
            after.Style = null;
            after.Points = new[]
            {
                new[] { from.X, from.Y }, new[] { control.X, control.Y }, new[] { to.X, to.Y },
            };
        }
        else
        {
            after.Type = AnnotationData.TypeArrow;
            after.Style = style.ToString();
            after.Points = new[] { new[] { from.X, from.Y }, new[] { to.X, to.Y } };
        }

        ApplyArrowStyleData(arrow, after);
        Push(new EditorAction(
            undo: () => { ApplyArrowStyleData(arrow, before); if (ReferenceEquals(_selected, arrow)) UpdateSelectionVisual(); },
            redo: () => { ApplyArrowStyleData(arrow, after); if (ReferenceEquals(_selected, arrow)) UpdateSelectionVisual(); }),
            apply: false);
        UpdateSelectionVisual();
    }

    /// <summary>
    /// Which end of a drag the head lands on. Dragging from the thing you are pointing AT
    /// toward the label is the other natural gesture, and "Inverse arrow direction" is the
    /// preference for people who draw that way; Alt flips it for the one arrow that wants
    /// the other order, without a trip to Settings — which is exactly what that setting's
    /// own hint has always promised.
    ///
    /// DELIBERATE DEVIATION: the sweep report says Cmd-drag, i.e. Ctrl on Windows. Ctrl is
    /// already the override for object snap and crop snap in this same window (see
    /// EditorWindow.Input.cs), so one key would mean two things mid-drag. Alt is free, and
    /// the Settings hint names Alt to match.
    /// </summary>
    private (Point Tail, Point Head) ArrowEnds(Point from, Point to) =>
        AnnotationFactory.ArrowEnds(
            from, to,
            _settings.Current.InverseArrowDirection,
            (Keyboard.Modifiers & ModifierKeys.Alt) != 0);

    /// <summary>
    /// Double-clicking an arrow steps it to the next style — the gesture for "not that
    /// one, the next one" without opening the style menu five times. Curved is in the
    /// cycle because it is a row in the same menu; the conversion is the same one the
    /// menu performs.
    /// </summary>
    private bool CycleSelectedArrowStyle()
    {
        if (_selected is not Path arrow || arrow.Tag is not AnnotationData meta ||
            meta.Type is not (AnnotationData.TypeArrow or AnnotationData.TypeCurvedArrow))
            return false;

        ArrowStyle current = meta.Type == AnnotationData.TypeCurvedArrow
            ? ArrowStyle.Curved
            : AnnotationFactory.ParseArrowStyle(meta.Style);
        ArrowStyle next = AnnotationFactory.NextArrowStyle(current);

        // Through the same entry point the style menu uses, so the tool arming and the
        // straight/curved conversion stay in one place.
        ApplyArrowStyleChoice(next);
        return true;
    }

    /// <summary>Rebuilds an arrow's geometry from its stored points + style and re-stores the metadata.</summary>
    private static void ApplyArrowStyleData(Path arrow, AnnotationData meta)
    {
        if (meta.Points is { } pts && pts.Length >= 2)
        {
            Point from = new(pts[0][0], pts[0][1]);
            Point to = new(pts[^1][0], pts[^1][1]);
            double thickness = meta.Thickness ?? arrow.StrokeThickness;
            StrokeRender render = StrokeStyles.ParseRender(meta.Sketch);
            int seed = meta.Seed ?? 0;
            if (meta.Type == AnnotationData.TypeCurvedArrow && pts.Length >= 3)
            {
                arrow.Data = AnnotationFactory.CurvedArrowGeometry(
                    from, new Point(pts[1][0], pts[1][1]), to, thickness, render, seed);
            }
            else
            {
                ArrowStyle style = AnnotationFactory.ParseArrowStyle(meta.Style);
                arrow.Data = AnnotationFactory.ArrowGeometry(from, to, thickness, style, render, seed);
                // The fancy arrow is one filled silhouette; a stroke on top of it would
                // fatten the tail back into a rounded line.
                arrow.StrokeThickness = style == ArrowStyle.Fancy ? 0 : thickness;
            }
        }
        arrow.Tag = meta;
    }

    // ----------------------------------------------------- opacity (parity tail)

    /// <summary>
    /// Opacity slider (25–100%): sets the alpha multiplier used for new marks and previews it
    /// live on the selected annotation. A continuous drag updates the visual on every tick but
    /// records a SINGLE undo entry, committed when the drag ends (see
    /// <see cref="OnOpacitySliderReleased"/>). The alpha is baked into the annotation's color,
    /// so it persists.
    /// </summary>
    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double pct = Math.Clamp(e.NewValue, 25, 100);
        _opacity = pct / 100.0;
        if (OpacityValue is not null)
            OpacityValue.Text = $"{Math.Round(pct)}%";
        if (!IsLoaded) return;
        // A tool switch drives the slider too; only a real user edit updates the memory.
        if (_syncingThicknessButtons) return;
        _toolPrefs.Update(_tool, s => s.WithBorderOpacity((int)Math.Round(pct)));

        // Apply live to the selection without pushing undo, capturing a one-time "before" for
        // the whole gesture so the eventual commit is a single reversible step.
        if (_selected is FrameworkElement fe && fe.Tag is AnnotationData meta && CanSetOpacity(_selected))
        {
            if (_opacityBefore is null || !ReferenceEquals(_opacityElement, _selected))
            {
                CommitOpacityGesture(); // flush any pending gesture on a different element first
                _opacityBefore = SnapshotStyle(_selected, meta);
                _opacityElement = _selected;
            }
            var after = SnapshotStyle(_selected, meta).With(null, null, null, _opacity);
            ApplyStyleSnapshot(_selected, after);
            UpdateSelectionVisual();
        }
    }

    /// <summary>Slider drag/keyboard release: commits the pending opacity gesture as one undo entry.</summary>
    private void OnOpacitySliderReleased(object sender, RoutedEventArgs e) => CommitOpacityGesture();

    /// <summary>
    /// Finalizes an in-progress opacity drag: records one undoable action from the captured
    /// "before" snapshot to the element's current (live-previewed) state. No-op when nothing
    /// changed or no gesture is pending.
    /// </summary>
    private void CommitOpacityGesture()
    {
        if (_opacityBefore is not { } before || _opacityElement is not FrameworkElement fe ||
            fe.Tag is not AnnotationData meta)
        {
            _opacityBefore = null;
            _opacityElement = null;
            return;
        }
        UIElement el = _opacityElement;
        _opacityBefore = null;
        _opacityElement = null;

        var after = SnapshotStyle(el, meta);
        if (after.StrokeColor == before.StrokeColor) return; // alpha unchanged → no entry

        Push(new EditorAction(
            undo: () => { ApplyStyleSnapshot(el, before); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); },
            redo: () => { ApplyStyleSnapshot(el, after); if (ReferenceEquals(_selected, el)) UpdateSelectionVisual(); }),
            apply: false);
    }

    /// <summary>Applies the current opacity multiplier to a base color's alpha channel.</summary>
    private static Color WithOpacity(Color c, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(c.A * opacity), 0, 255), c.R, c.G, c.B);

    // ------------------------------------------- blur/pixelate strength (parity tail)

    // ------------------------------------------------------------- redaction style

    private void OnRedactStyleClick(object sender, RoutedEventArgs e)
    {
        BuildRedactStyleRows();
        CheckRowByTag(RedactStyleList, SelectedRedactStyle() ?? _redactStyle);
        OpenPopover(RedactStylePopup, RedactStyleList);
    }

    private RedactStyle? SelectedRedactStyle() =>
        _selected is FrameworkElement fe && fe.Tag is AnnotationData meta &&
        meta.Type == AnnotationData.TypeRedact
            ? Redaction.Parse(meta.Style)
            : null;

    private void BuildRedactStyleRows()
    {
        if (RedactStyleList.Children.Count > 0) return;
        (RedactStyle Style, string Label)[] rows =
        {
            (RedactStyle.Pixelate, "Pixelate"),
            (RedactStyle.BlurSecure, "Blur (secure)"),
            (RedactStyle.BlurSmooth, "Blur (smooth)"),
            (RedactStyle.BlackOut, "Black out"),
        };
        foreach ((RedactStyle style, string label) in rows)
        {
            var row = AddPopoverRow(RedactStyleList, "RedactStyle",
                new TextBlock { Text = label }, style, label);
            RedactStyle picked = style;
            row.Click += (_, _) =>
            {
                ApplyRedactStyle(picked);
                RedactStylePopup.IsOpen = false;
            };
        }
    }

    /// <summary>
    /// Restyles the selected redaction, and sets the style for the next one either way.
    /// Black Out takes the strength slider off the toolbar entirely — a black rectangle
    /// has no strength, and greying the control out would only leave dead space.
    /// </summary>
    private void ApplyRedactStyle(RedactStyle style)
    {
        RestyleSelectedRedaction(style, strength: null, push: true);
        _redactStyle = style;
        UpdateContextPanels();
    }

    private void OnRedactStrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int value = Redaction.ClampStrength((int)Math.Round(e.NewValue));
        if (RedactStrengthValue is not null)
            RedactStrengthValue.Text = value.ToString(CultureInfo.InvariantCulture);
        if (!IsLoaded || _syncingThicknessButtons) return;
        _redactStrength = value;

        // Live preview on the selection, with ONE undo entry for the whole drag: the
        // "before" is captured at the first tick and committed when the slider is let go.
        if (_selected is FrameworkElement fe && fe.Tag is AnnotationData meta &&
            meta.Type == AnnotationData.TypeRedact)
        {
            if (_redactStrengthBefore is null || !ReferenceEquals(_redactStrengthElement, _selected))
            {
                CommitRedactStrengthGesture();
                _redactStrengthBefore = meta.Clone();
                _redactStrengthElement = _selected;
            }
            RestyleSelectedRedaction(style: null, strength: value, push: false);
        }
    }

    private void OnRedactStrengthReleased(object sender, RoutedEventArgs e) => CommitRedactStrengthGesture();

    private void CommitRedactStrengthGesture()
    {
        if (_redactStrengthBefore is not { } before ||
            _redactStrengthElement is not FrameworkElement fe || fe.Tag is not AnnotationData after)
        {
            _redactStrengthBefore = null;
            _redactStrengthElement = null;
            return;
        }
        UIElement el = _redactStrengthElement;
        _redactStrengthBefore = null;
        _redactStrengthElement = null;
        if (before.Strength == after.Strength) return;

        Push(new EditorAction(
            undo: () => { fe.Tag = before; RefreshRedaction(el); },
            redo: () => { fe.Tag = after; RefreshRedaction(el); }), apply: false);
    }

    // ------------------------------------------------------- magnification

    /// <summary>
    /// The magnification dial. Live on the selection with ONE undo entry for the whole
    /// drag — the "before" is captured on the first tick and committed when the slider is
    /// let go — exactly as the redaction strength works, because it is the same gesture.
    /// </summary>
    private void OnMagnifyChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double value = MagnifierLayout.ClampScale(e.NewValue);
        if (MagnifyValueLabel is not null)
            MagnifyValueLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{value:0.#}x");
        if (!IsLoaded || _syncingThicknessButtons) return;
        _magnification = value;

        if (_selected is FrameworkElement fe && fe.Tag is AnnotationData meta &&
            meta.Type == AnnotationData.TypeMagnifier)
        {
            if (_magnifyBefore is null || !ReferenceEquals(_magnifyElement, _selected))
            {
                CommitMagnifyGesture();
                _magnifyBefore = meta.Clone();
                _magnifyElement = _selected;
            }
            // Clone-and-assign rather than mutate: the committed redo closure captures
            // whatever Tag holds, so mutating the live instance made the FIRST drag's redo
            // point at the SECOND drag's zoom. Same shape as RestyleSelectedRedaction.
            var next = meta.Clone();
            next.Zoom = value;
            fe.Tag = next;
            RefreshRedaction(fe);
        }
    }

    private void OnMagnifyReleased(object sender, RoutedEventArgs e) => CommitMagnifyGesture();

    private void CommitMagnifyGesture()
    {
        if (_magnifyBefore is not { } before ||
            _magnifyElement is not FrameworkElement fe || fe.Tag is not AnnotationData after)
        {
            _magnifyBefore = null;
            _magnifyElement = null;
            return;
        }
        UIElement el = _magnifyElement;
        _magnifyBefore = null;
        _magnifyElement = null;
        if (before.Zoom == after.Zoom) return;

        Push(new EditorAction(
            undo: () => { fe.Tag = before; RefreshRedaction(el); },
            redo: () => { fe.Tag = after; RefreshRedaction(el); }), apply: false);
    }

    /// <summary>Pulls the selected callout's own magnification into the dial.</summary>
    private void SyncMagnifySlider()
    {
        if (MagnifySlider is null) return;
        double value = _selected is FrameworkElement fe && fe.Tag is AnnotationData meta &&
            meta.Type == AnnotationData.TypeMagnifier
                ? MagnifierLayout.ClampScale(meta.Zoom ?? MagnifierLayout.DefaultScale)
                : _magnification;

        _syncingThicknessButtons = true;
        try
        {
            MagnifySlider.Value = value;
            MagnifyValueLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{value:0.#}x");
        }
        finally
        {
            _syncingThicknessButtons = false;
        }
    }

    private void ApplyCropRatio(string tag)
    {
        _cropRatioTag = tag;
        if (string.IsNullOrEmpty(tag))
        {
            _cropRatio = null;
            return;
        }
        string[] parts = tag.Split(':');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
            w > 0 && h > 0)
        {
            _cropRatio = w / h;
        }
    }

    // ------------------------------------------------------------ colour picker

    /// <summary>
    /// The colour well opens the picker. Nine inline radio swatches used to live in the
    /// toolbar; they are the picker's grid now, so the row carries one control instead of
    /// eleven and the current colour is legible at a glance.
    /// </summary>
    private void OnColorWellClick(object sender, RoutedEventArgs e)
    {
        BuildColorPickerPalette();
        // Shapes are the only marks with two colours, so the Fill tab only offers itself
        // where it means something.
        bool canFill = ToolPreferences.SupportsFill(_tool);
        FillTabBtn.IsEnabled = canFill;
        if (!canFill && _pickerFillTarget)
        {
            _pickerFillTarget = false;
            BorderTabBtn.IsChecked = true;
        }
        SyncColorPickerInputs(CurrentTargetColor());
        RebuildSavedColorsRow();
        BuildColorCopyMenu();
        OpenPopover(ColorPickerPopup, ColorPickerGrid);
    }

    /// <summary>Whichever colour the open tab is editing.</summary>
    private Color CurrentTargetColor() =>
        _pickerFillTarget ? _fillColor ?? _color : _color;

    private void OnColorTargetChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _pickerFillTarget = FillTabBtn.IsChecked == true;
        SyncColorPickerInputs(CurrentTargetColor());
    }

    /// <summary>
    /// Sixteen swatches, two rows of eight: WinShot's annotation colours plus the greys and
    /// tints people actually reach for. The old grid was forty, which is a palette to browse
    /// rather than a palette to pick from.
    /// </summary>
    private void BuildColorPickerPalette()
    {
        if (_colorPickerBuilt) return;
        _colorPickerBuilt = true;

        string[] swatches =
        {
            "#FF453A", "#FF9500", "#FFCC00", "#34C759", "#0A84FF", "#5856D6", "#AF52DE", "#FF2D55",
            "#000000", "#3A3A3C", "#8E8E93", "#C7C7CC", "#FFFFFF", "#00C7BE", "#A2845E", "#D70015",
        };

        foreach (string hex in swatches)
        {
            if (!TryParseColor(hex, out var c)) continue;
            ColorPickerGrid.Children.Add(BuildSwatchButton(c, hex, saved: false));
        }
    }

    private Button BuildSwatchButton(Color c, string label, bool saved)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("SwatchButton"),
            // How a saved colour is removed has to be on screen: HelpText is read by screen
            // readers and by nobody else, so a right-click nobody is told about is no way out.
            ToolTip = saved ? $"{label} — right-click to forget it" : label,
            Tag = c,
            Background = new SolidColorBrush(c),
        };
        AutomationProperties.SetName(btn, $"Color {label}");
        AutomationProperties.SetHelpText(btn, saved
            ? $"Use saved color {label}. Right-click to forget it."
            : $"Use annotation color {label}.");
        btn.Click += OnColorPickerGridPick;
        if (saved)
        {
            // Picking a saved colour is a vote for it: SavedColors.With moves it back to the
            // head. The built-in palette deliberately does not vote — those sixteen cannot be
            // lost, and running them through a ten-slot list would evict the ones that can.
            btn.Click += (_, _) => RememberColor(c);
            btn.MouseRightButtonUp += (_, _) =>
            {
                ForgetColor(c);
                RebuildSavedColorsRow();
            };
        }
        return btn;
    }

    /// <summary>
    /// The "My colours" row. Empty until something is saved, so it never shows a gap — and
    /// the first save eases the section in on the sub-row recipe instead of inserting it in
    /// one frame under the pointer. Save itself is docked right, so it never moves.
    ///
    /// The list is <see cref="WinShot.Core.SavedColors"/> over <c>Settings.SavedColors</c> —
    /// the same store the capture-side picker writes — so a colour saved here survives the
    /// window that saved it. A second in-editor list lived here until that store existed and
    /// would only ever have disagreed with it.
    /// </summary>
    private void RebuildSavedColorsRow()
    {
        SavedColorsGrid.Children.Clear();
        foreach (string hex in WinShot.Core.SavedColors.Sanitize(_settings.Current.SavedColors))
        {
            if (TryParseColor(hex, out Color c))
                SavedColorsGrid.Children.Add(BuildSwatchButton(c, hex, saved: true));
        }

        Visibility show = SavedColorsGrid.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool appearing = show == Visibility.Visible && SavedColorsSection.Visibility != Visibility.Visible;
        SavedColorsSection.Visibility = show;
        if (appearing)
            SavedColorsSection.BeginStoryboard((Storyboard)FindResource("SubRowIn"));
    }

    /// <summary>Drops one colour from the user's palette. The inverse of RememberColor, and
    /// the only way out of the row — which is why the swatch's tooltip says so out loud.</summary>
    private void ForgetColor(Color c)
    {
        string hex = WinShot.Core.ColorFormatter.Format(
            System.Drawing.Color.FromArgb(c.R, c.G, c.B), WinShot.Core.ColorFormat.Hex);
        List<string> next = WinShot.Core.SavedColors.Sanitize(_settings.Current.SavedColors);
        if (next.RemoveAll(e => string.Equals(e, hex, StringComparison.OrdinalIgnoreCase)) == 0) return;
        _settings.Current.SavedColors = next;
        _savedColorsDirty = true;
    }

    private void OnSaveColor(object sender, RoutedEventArgs e)
    {
        RememberColor(PickerColor());
        RebuildSavedColorsRow();
    }

    private void OnColorPickerGridPick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is Color c)
        {
            SyncColorPickerInputs(c);
            CommitPickerColor();
        }
    }

    // ---- saturation / value square

    private Size SvSize() => new(
        Math.Max(1, SvSquareBorder.ActualWidth - 2),
        Math.Max(1, SvSquareBorder.ActualHeight - 2));

    private void OnSvSquareDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = true;
        SvSquareBorder.CaptureMouse();
        UpdateSvFromPointer(e.GetPosition(SvSquareBorder));
    }

    private void OnSvSquareMove(object sender, MouseEventArgs e)
    {
        if (_draggingSv) UpdateSvFromPointer(e.GetPosition(SvSquareBorder));
    }

    private void OnSvSquareUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingSv) return;
        _draggingSv = false;
        SvSquareBorder.ReleaseMouseCapture();
        CommitPickerColor();
    }

    /// <summary>
    /// Closing the popup mid-drag (Escape in the hex box) unloads the child and drops the
    /// capture without a MouseUp; the flag has to come down with it, or the thumb tracks the
    /// pointer with no button held the next time the picker is opened.
    /// </summary>
    private void OnSvSquareLostCapture(object sender, MouseEventArgs e) => _draggingSv = false;

    /// <summary>
    /// The thumb's position is a fraction of the square, and the square has no size until the
    /// popup has laid out — so the first paint put the marker in the top-left corner whatever
    /// colour was current. Repaint once the square knows how big it is.
    /// </summary>
    private void OnSvSquareSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ColorPickerHex is not null) RefreshPickerVisual();
    }

    private void UpdateSvFromPointer(Point p)
    {
        (_pickerSaturation, _pickerValue) = ColorPickerMath.FromPosition(p, SvSize());
        RefreshPickerVisual();
    }

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressPickerEvents || !IsLoaded) return;
        _pickerHue = e.NewValue;
        RefreshPickerVisual();
        CommitPickerColor();
    }

    private void OnAlphaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressPickerEvents || !IsLoaded) return;
        _pickerAlpha = (byte)Math.Clamp(Math.Round(e.NewValue * 2.55), 0, 255);
        RefreshPickerVisual();
        CommitPickerColor();
    }

    private Color PickerColor() =>
        ColorPickerMath.FromHsv(_pickerHue, _pickerSaturation, _pickerValue, _pickerAlpha);

    /// <summary>Repaints the square's hue wash, the thumb, the preview and the hex box.</summary>
    private void RefreshPickerVisual()
    {
        Color pure = ColorPickerMath.FromHsv(_pickerHue, 1, 1);
        SvHueLayer.Fill = new LinearGradientBrush(Colors.White, pure, 0);

        Point thumb = ColorPickerMath.PositionFor(_pickerSaturation, _pickerValue, SvSize());
        Canvas.SetLeft(SvThumb, thumb.X - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, thumb.Y - SvThumb.Height / 2);

        Color colour = PickerColor();
        // The thumb sits on a plate that runs white to black to the picked hue, so its ring
        // is derived from where it is rather than fixed: a white ring is invisible in the
        // white corner, which is exactly where people go for white and light greys. Alpha is
        // dropped for the thumb — it marks a position in the square, and a 0-alpha marker
        // that cannot be seen is not a marker.
        Color opaque = Color.FromRgb(colour.R, colour.G, colour.B);
        SvThumb.Fill = new SolidColorBrush(opaque);
        SvThumb.Stroke = new SolidColorBrush(ColorMath.TextOn(opaque));
        ColorPickerPreview.Fill = new SolidColorBrush(colour);

        // The alpha track ramps from nothing to the colour it is the alpha OF, so which end
        // is see-through is on the control instead of in the user's head.
        var ramp = new LinearGradientBrush(Color.FromArgb(0, opaque.R, opaque.G, opaque.B), opaque, 0);
        ramp.Freeze();
        AlphaSlider.Background = ramp;
        AlphaValue.Text = $"{Math.Round(_pickerAlpha / 2.55):0}%";

        _suppressHexEvents = true;
        try { ColorPickerHex.Text = ColorPickerMath.ToHex(colour); }
        finally { _suppressHexEvents = false; }
    }

    /// <summary>
    /// Points every control in the picker at one colour without treating it as an edit.
    /// A fully desaturated colour has no meaningful hue, so the slider is left where it is
    /// rather than snapping to red the moment the user drags into the white corner.
    /// </summary>
    private void SyncColorPickerInputs(Color c)
    {
        (double hue, double saturation, double value) = ColorPickerMath.ToHsv(c);
        if (saturation > 0.001) _pickerHue = hue;
        _pickerSaturation = saturation;
        _pickerValue = value;
        _pickerAlpha = c.A;

        _suppressPickerEvents = true;
        try
        {
            HueSlider.Value = _pickerHue;
            AlphaSlider.Value = Math.Round(_pickerAlpha / 2.55);
        }
        finally
        {
            _suppressPickerEvents = false;
        }
        RefreshPickerVisual();
    }

    /// <summary>Hex box: live-preview a valid value as the user types (without closing the popup).</summary>
    private void OnColorPickerHexChanged(object sender, TextChangedEventArgs e)
    {
        // Fires once during XAML parse (initial Text) before the preview rect exists.
        if (_suppressHexEvents || ColorPickerPreview is null) return;
        if (ColorPickerHex.Text is string s && TryParseHexLoose(s, out var c))
            ColorPickerPreview.Fill = new SolidColorBrush(c);
    }

    private void OnColorPickerHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHexColor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ColorPickerPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnColorPickerApply(object sender, RoutedEventArgs e)
    {
        CommitPickerColor();
        ColorPickerPopup.IsOpen = false;
    }

    private void CommitHexColor()
    {
        if (ColorPickerHex.Text is string s && TryParseHexLoose(s, out var c))
        {
            SyncColorPickerInputs(c);
            CommitPickerColor();
            ColorPickerPopup.IsOpen = false;
        }
    }

    /// <summary>
    /// The formats a colour can be copied in, and their menu labels. Bare hex and OKLCH are
    /// here because the two places a colour ends up are a CSS file and a chat message, and
    /// neither wants the app's own #RRGGBB every time.
    /// </summary>
    private static readonly (string Label, WinShot.Core.ColorFormat Format)[] CopyFormats =
    {
        ("HEX", WinShot.Core.ColorFormat.Hex),
        ("HEX without #", WinShot.Core.ColorFormat.HexBare),
        ("HEX with alpha", WinShot.Core.ColorFormat.HexArgb),
        ("rgb()", WinShot.Core.ColorFormat.Rgb),
        ("hsl()", WinShot.Core.ColorFormat.Hsl),
        ("OKLCH", WinShot.Core.ColorFormat.Oklch),
    };

    /// <summary>Builds the preview swatch's "Copy as" menu once.</summary>
    private void BuildColorCopyMenu()
    {
        if (ColorPickerPreviewButton.ContextMenu is not null) return;

        // Placed against the swatch, not the mouse: ContextMenu defaults to MousePoint, so
        // Enter or Space on the focused swatch dropped the format list wherever the pointer
        // happened to be sitting -- possibly off the popup entirely.
        // Both styles applied by hand: DarkContextMenu and DarkMenuItem are keyed, not
        // implicit, so a menu built in code and left unstyled falls back to Aero2 -- a white
        // popup with black text hanging off a swatch inside the dark picker.
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            PlacementTarget = ColorPickerPreviewButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var itemStyle = (Style)FindResource("DarkMenuItem");
        foreach ((string label, WinShot.Core.ColorFormat format) in CopyFormats)
        {
            var item = new MenuItem { Header = $"Copy as {label}", Tag = format, Style = itemStyle };
            // The automation name is the visible label, so voice control can say it.
            AutomationProperties.SetName(item, $"Copy as {label}");
            item.Click += OnCopyColorAs;
            menu.Items.Add(item);
        }
        ColorPickerPreviewButton.ContextMenu = menu;
    }

    /// <summary>Opens the format list from the swatch. It is a Button, so Tab reaches it and
    /// Enter, Space, Shift+F10 and the Apps key all land here or on its context menu.</summary>
    private void OnColorPickerPreviewClick(object sender, RoutedEventArgs e)
    {
        BuildColorCopyMenu();
        if (ColorPickerPreviewButton.ContextMenu is ContextMenu menu)
            menu.IsOpen = true;
    }

    /// <summary>
    /// Copies the picked colour in one notation. Awaited and confirmed: the clipboard write
    /// retries a busy clipboard and then faults, and discarding that Task made "Copy as
    /// OKLCH" a menu row that said nothing whether it worked or not.
    /// </summary>
    private async void OnCopyColorAs(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not WinShot.Core.ColorFormat format)
            return;

        Color c = _customColor;
        string text = WinShot.Core.ColorFormatter.Format(
            System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B), format);
        try
        {
            await CaptureService.SetTextToClipboardAsync(text);
            // Success, so the checkmark glyph -- the info glyph is what "Save failed" and
            // "Copied" looking identical was about.
            App.Toast("Copied", text, severity: WinShot.Core.ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            WinShot.Core.Log.Error("Could not copy the color", ex);
            App.Toast("Copy failed", WinShot.Core.FriendlyError.For(ex), severity: WinShot.Core.ToastSeverity.Error);
        }
    }

    /// <summary>
    /// The eyedropper inside the picker: arms the sampling tool, closes the popup, and
    /// reopens it on the sampled colour — so picking a colour off the image is part of
    /// choosing one rather than a detour that loses the picker.
    /// </summary>
    private void OnPickerEyedropper(object sender, RoutedEventArgs e)
    {
        ColorPickerPopup.IsOpen = false;
        _reopenPickerAfterSample = true;
        CheckToolButton(EditorTool.Eyedropper);
    }

    /// <summary>Applies the picker's colour to whichever tab is open.</summary>
    private void CommitPickerColor()
    {
        Color c = PickerColor();
        if (_pickerFillTarget) ApplyFillColor(c);
        else ApplyCustomColor(c);
    }

    /// <summary>
    /// Persists a custom color into the current-color indicator, makes it the active stroke
    /// color, and (when an annotation is selected) restyles it via the normal path. Also
    /// used by the eyedropper so a sampled color lands in the same place.
    /// </summary>
    private void ApplyCustomColor(Color c)
    {
        _customColor = c;
        SetCurrentColor(c); // the well repaints from here
    }

    /// <summary>
    /// The fill colour for shapes. Null until the Fill tab is used, and null means "derive
    /// the fill from the stroke" — which is what every shape drawn before this existed did,
    /// so nothing already on a canvas changes colour.
    /// </summary>
    private void ApplyFillColor(Color c)
    {
        _fillColor = c;
        _toolPrefs.Update(_tool, s => s.WithFillColor(c));
        if (IsLoaded) RestyleSelectedFillColor(c);
    }

    /// <summary>
    /// Records a picked colour in the user's palette. Held in memory and written once when the
    /// editor closes: SettingsService.Save writes the file and raises Changed (which
    /// re-registers every global hotkey), and the eyedropper calls this on every sample.
    /// </summary>
    private void RememberColor(Color c)
    {
        var picked = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        List<string> next = WinShot.Core.SavedColors.With(_settings.Current.SavedColors, picked);
        if (next.SequenceEqual(WinShot.Core.SavedColors.Sanitize(_settings.Current.SavedColors), StringComparer.OrdinalIgnoreCase))
            return;

        _settings.Current.SavedColors = next;
        _savedColorsDirty = true;
    }

    /// <summary>Writes the palette out, once, when the editor is done with it.</summary>
    private void PersistSavedColors()
    {
        if (!_savedColorsDirty) return;
        _savedColorsDirty = false;
        // Quietly: the palette is runtime state nothing subscribes to, and a plain Save()
        // raises Changed, which unregisters and re-registers every global hotkey.
        try { _settings.SaveQuietly(); }
        catch (Exception ex) { WinShot.Core.Log.Error("Could not save the color palette (non-fatal)", ex); }
    }

    /// <summary>Parses "#RGB", "#RRGGBB", "#AARRGGBB" or bare hex; tolerates a missing leading '#'.</summary>
    private static bool TryParseHexLoose(string text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        try
        {
            if (ColorConverter.ConvertFromString(s) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch
        {
            // invalid hex string — leave color as the default and report failure
        }
        return false;
    }

}
