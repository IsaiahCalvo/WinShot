using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

/// <summary>
/// The thumbnail card's right-click menu: which rows it shows, and the image and
/// shell operations behind them. Split out from <see cref="FastQuickActionsWindow"/>
/// so the row layout and the geometry are testable without a window.
/// </summary>
internal static class QuickActionsMenu
{
    /// <summary>Row id for a separator line.</summary>
    internal const string Separator = "-";

    internal const string Annotate = "annotate";
    internal const string Pin = "pin";
    internal const string ExtractText = "ocr";
    internal const string Background = "background";
    internal const string RotateLeft = "rotate-left";
    internal const string RotateRight = "rotate-right";
    internal const string FlipHorizontal = "flip-h";
    internal const string FlipVertical = "flip-v";
    internal const string Resize = "resize";
    internal const string Print = "print";
    internal const string Save = "save";
    internal const string SaveAs = "save-as";
    internal const string Open = "open";
    internal const string OpenWith = "open-with";
    internal const string ShowInFolder = "show-in-folder";
    internal const string MoveToRecycleBin = "recycle";
    internal const string Share = "share";
    internal const string Close = "close";

    internal readonly record struct Row(string Id, string Text, string Shortcut = "");

    /// <summary>
    /// The menu, in order. <paramref name="mediaFile"/> is the recording card, which
    /// fronts a finished mp4/gif: no pixel edits, and Open/Edit act on the file.
    /// </summary>
    internal static IReadOnlyList<Row> Rows(bool mediaFile, bool canEdit, bool canShare)
    {
        var rows = new List<Row>();

        if (mediaFile)
        {
            rows.Add(new Row(Open, "Open"));
            if (canEdit)
                rows.Add(new Row(Annotate, "Edit video", "Ctrl+E"));
            rows.Add(new Row(Separator, Separator));
            rows.Add(new Row(SaveAs, "Save a copy…", "Ctrl+S"));
        }
        else
        {
            rows.Add(new Row(Annotate, "Open Annotate tool", "Ctrl+E"));
            rows.Add(new Row(Pin, "Pin to the screen", "P"));
            rows.Add(new Row(ExtractText, "Extract text", "O"));
            rows.Add(new Row(Background, "Add background", "B"));
            rows.Add(new Row(Separator, Separator));
            rows.Add(new Row(RotateLeft, "Rotate left"));
            rows.Add(new Row(RotateRight, "Rotate right"));
            rows.Add(new Row(FlipHorizontal, "Flip horizontal"));
            rows.Add(new Row(FlipVertical, "Flip vertical"));
            rows.Add(new Row(Resize, "Resize…"));
            rows.Add(new Row(Separator, Separator));
            rows.Add(new Row(Print, "Print…"));
            rows.Add(new Row(Save, "Save", "Ctrl+S"));
            rows.Add(new Row(SaveAs, "Save as…"));
        }

        rows.Add(new Row(Separator, Separator));
        rows.Add(new Row(OpenWith, "Open with…"));
        rows.Add(new Row(ShowInFolder, "Show in folder"));
        rows.Add(new Row(MoveToRecycleBin, "Move to Recycle Bin"));

        if (canShare)
        {
            rows.Add(new Row(Separator, Separator));
            rows.Add(new Row(Share, "Share…"));
        }

        rows.Add(new Row(Separator, Separator));
        rows.Add(new Row(Close, "Close", "Ctrl+W"));
        return rows;
    }

    /// <summary>
    /// The transform for a rotate/flip row. Returns null for rows that are not one.
    /// <see cref="SD.Image.RotateFlip"/> works in place and swaps the dimensions on a
    /// quarter turn, so the card keeps the same bitmap instance.
    /// </summary>
    internal static SD.RotateFlipType? TransformFor(string id) => id switch
    {
        RotateLeft => SD.RotateFlipType.Rotate270FlipNone,
        RotateRight => SD.RotateFlipType.Rotate90FlipNone,
        FlipHorizontal => SD.RotateFlipType.RotateNoneFlipX,
        FlipVertical => SD.RotateFlipType.RotateNoneFlipY,
        _ => null,
    };

    /// <summary>
    /// Undoing a rotate or a flip is just another rotate or flip, so those rows cost
    /// no memory to reverse — unlike Resize…, which has to keep the old bitmap.
    /// </summary>
    internal static SD.RotateFlipType Inverse(SD.RotateFlipType transform) => transform switch
    {
        SD.RotateFlipType.Rotate90FlipNone => SD.RotateFlipType.Rotate270FlipNone,
        SD.RotateFlipType.Rotate270FlipNone => SD.RotateFlipType.Rotate90FlipNone,
        _ => transform, // a flip is its own inverse
    };

    /// <summary>
    /// The glyph for a row, or null for the text-only rows. CleanShot icons only its
    /// first two groups — the capture actions and the pixel edits — and so do we.
    /// </summary>
    internal static string? IconFor(string id) => id switch
    {
        Annotate => "quick-access-edit.svg",
        Pin => "quick-access-pin.svg",
        ExtractText => "quick-access-text.svg",
        Background => "quick-access-background.svg",
        RotateLeft => "quick-access-rotate-left.svg",
        RotateRight => "quick-access-rotate-right.svg",
        FlipHorizontal => "quick-access-flip-horizontal.svg",
        FlipVertical => "quick-access-flip-vertical.svg",
        Resize => "quick-access-resize.svg",
        _ => null,
    };

    internal const string UndoIcon = "quick-access-undo.svg";

    /// <summary>Height that keeps <paramref name="source"/>'s aspect at <paramref name="width"/>.</summary>
    internal static int AspectHeight(SD.Size source, int width) =>
        source.Width <= 0 ? 1 : Math.Max(1, (int)Math.Round(width * (double)source.Height / source.Width));

    /// <summary>Width that keeps <paramref name="source"/>'s aspect at <paramref name="height"/>.</summary>
    internal static int AspectWidth(SD.Size source, int height) =>
        source.Height <= 0 ? 1 : Math.Max(1, (int)Math.Round(height * (double)source.Width / source.Height));

    /// <summary>Largest centered rect of <paramref name="source"/>'s aspect that fits <paramref name="bounds"/>.</summary>
    internal static SD.Rectangle FitCentered(SD.Size source, SD.Rectangle bounds)
    {
        if (source.Width <= 0 || source.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return bounds;
        double scale = Math.Min(bounds.Width / (double)source.Width, bounds.Height / (double)source.Height);
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new SD.Rectangle(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
    }

    internal static SD.Bitmap Resized(SD.Bitmap source, int width, int height)
    {
        var scaled = new SD.Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppPArgb);
        using var g = SD.Graphics.FromImage(scaled);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new SD.Rectangle(0, 0, scaled.Width, scaled.Height));
        return scaled;
    }

    /// <summary>The Windows "Open with…" picker for a file — the shell owns the dialog.</summary>
    internal static void ShowOpenWith(string path) =>
        Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"")
        {
            UseShellExecute = true,
        });

    internal static void ShowInExplorer(string path) =>
        Process.Start("explorer.exe", $"/select,\"{path}\"");

    internal static void SendToRecycleBin(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    /// <summary>
    /// Prints one image through the standard printer dialog, fitted to the page's
    /// printable area. A clone is taken because the print runs after this returns.
    /// </summary>
    internal static void PrintImage(WF.IWin32Window owner, SD.Bitmap image)
    {
        var copy = (SD.Bitmap)image.Clone();
        var document = new PrintDocument();
        document.PrintPage += (_, e) =>
        {
            SD.Rectangle bounds = e.MarginBounds.Width > 0 && e.MarginBounds.Height > 0
                ? e.MarginBounds
                : e.PageBounds;
            e.Graphics!.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(copy, FitCentered(copy.Size, bounds));
            e.HasMorePages = false;
        };
        document.EndPrint += (_, _) =>
        {
            copy.Dispose();
            document.Dispose();
        };

        using var dialog = new WF.PrintDialog { Document = document, UseEXDialog = true };
        if (dialog.ShowDialog(owner) != WF.DialogResult.OK)
        {
            copy.Dispose();
            document.Dispose();
            return;
        }
        document.Print();
    }

    internal static bool FileReady(string? path) => path is not null && File.Exists(path);
}
