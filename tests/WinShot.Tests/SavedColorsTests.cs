using System.IO;
using System.Xml.Linq;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The user's own palette. Before this the custom colour was a field on the editor window,
/// so every session started from the same hard-coded red and a colour matched by eyedropper
/// died with the window.
/// </summary>
public class SavedColorsTests
{
    private static SD.Color Rgb(int r, int g, int b) => SD.Color.FromArgb(r, g, b);

    [Fact]
    public void APickedColourGoesToTheHead()
    {
        List<string> list = SavedColors.With(new[] { "#0A84FF" }, Rgb(255, 59, 48));
        Assert.Equal(new[] { "#FF3B30", "#0A84FF" }, list);
    }

    /// <summary>Re-picking a saved colour is a vote for it, not a second entry.</summary>
    [Fact]
    public void RepickingAColourMovesItRatherThanDuplicatingIt()
    {
        List<string> list = SavedColors.With(
            new[] { "#0A84FF", "#FF3B30", "#34C759" }, Rgb(255, 59, 48));

        Assert.Equal(new[] { "#FF3B30", "#0A84FF", "#34C759" }, list);
        Assert.Single(list, entry => entry == "#FF3B30");
    }

    [Fact]
    public void TheOldestFallsOffAtTheCap()
    {
        var list = new List<string>();
        for (int i = 0; i < 14; i++)
            list = SavedColors.With(list, Rgb(i * 17, 0, 0));

        Assert.Equal(SavedColors.MaxCount, list.Count);
        Assert.Equal("#DD0000", list[0]);                // 13 * 17
        Assert.DoesNotContain("#000000", list);          // the first one is gone
    }

    /// <summary>
    /// The settings file is user-editable and survives downgrades, so the stored list can
    /// hold anything. Sanitising it is what keeps the picker from rendering empty swatches
    /// or two of the same colour.
    /// </summary>
    [Fact]
    public void AStoredListIsNormalisedOnTheWayIn()
    {
        List<string> list = SavedColors.Sanitize(new[]
        {
            "ff3b30",       // bare and lower case
            "#FF3B30",      // the same colour again
            "not a colour",
            "#0a84ff",
            "",
        });

        Assert.Equal(new[] { "#FF3B30", "#0A84FF" }, list);
    }

    [Fact]
    public void AnEmptyOrMissingListIsFine()
    {
        Assert.Empty(SavedColors.Sanitize(null));
        Assert.Empty(SavedColors.Sanitize(Array.Empty<string>()));
        Assert.Equal(new[] { "#34C759" }, SavedColors.With(null, Rgb(52, 199, 89)));
    }

    /// <summary>A downgrade or a hand-edit could leave more than the cap in the file.</summary>
    [Fact]
    public void AnOverlongStoredListIsTrimmed()
    {
        string[] stored = Enumerable.Range(0, 30)
            .Select(i => ColorFormatter.Format(Rgb(i * 8, i * 3, 0), ColorFormat.Hex))
            .ToArray();

        Assert.Equal(SavedColors.MaxCount, SavedColors.Sanitize(stored).Count);
        Assert.Equal(SavedColors.MaxCount, SavedColors.With(stored, Rgb(1, 2, 3)).Count);
    }

    /// <summary>
    /// The cap and the layout have to agree, or a palette silently loses the colours it says
    /// it kept: the grid was once cut to eight columns to stop the slots clipping, halving a
    /// ten-colour palette to the eight the user could see and click. The editor's grid wraps
    /// instead of capping, so what it owes is a cell wide enough not to clip and enough cells
    /// at that pitch to draw the whole cap.
    /// </summary>
    [Fact]
    public void EverySavedSlotHasSomewhereToBeDrawn()
    {
        XDocument xaml = XDocument.Load(FindRepoFile("src", "WinShot", "Editor", "EditorWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement grid = xaml.Descendants()
            .Single(e => e.Name.LocalName == "UniformGrid" && (string?)e.Attribute(x + "Name") == "SavedColorsGrid");

        int columns = int.Parse((string)grid.Attribute("Columns")!);
        double width = double.Parse((string)grid.Attribute("Width")!);

        // 22 px swatch + 2 px margins a side, the same cell the palette above it uses.
        Assert.True(width / columns >= 26,
            $"{width} px over {columns} columns is {width / columns:0.#} px a cell, so the 26 px slots clip.");
        // A UniformGrid wraps to as many rows as it needs — unless Rows is pinned, which
        // would drop everything past columns x rows on the floor with nothing to say so.
        Assert.Null(grid.Attribute("Rows"));
    }

    private static string FindRepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(parts)}");
    }
}
