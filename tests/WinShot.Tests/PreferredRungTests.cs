using System.Drawing;
using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Which level of an app's own reported nesting gets highlighted. Chains arrive sorted
/// smallest-first, the way the detector hands them over.
/// </summary>
public class PreferredRungTests
{
    static AxNode Node(int w, int h, int role) => new(new Rectangle(0, 0, w, h), role);

    [Fact]
    public void PrefersTheButtonOverTheTextInsideIt()
    {
        var chain = new[]
        {
            Node(60, 16, AxNode.StaticText),  // the word on the button
            Node(96, 32, AxNode.PushButton),  // the button
            Node(400, 60, AxNode.Toolbar),
        };
        Assert.Equal(new Rectangle(0, 0, 96, 32), FastRegionSelectorDialog.PreferredRung(chain));
    }

    [Fact]
    public void PrefersTheRowOverTheCellTextAndOverTheListAroundIt()
    {
        var chain = new[]
        {
            Node(120, 14, AxNode.StaticText),
            Node(600, 40, AxNode.ListItem),
            Node(600, 900, AxNode.List),
        };
        Assert.Equal(new Rectangle(0, 0, 600, 40), FastRegionSelectorDialog.PreferredRung(chain));
    }

    [Fact]
    public void DoesNotPromoteARegionOverATighterRectBeneathIt()
    {
        // A page footer: static text in a group, inside the document. The group is a fine
        // wheel notch, but jumping straight to it puts a 1200x90 band around a 200x18 line.
        var chain = new[]
        {
            Node(200, 18, AxNode.Text),       // editable/inline text IS a thing
            Node(1200, 90, AxNode.Grouping),
            Node(1200, 4000, AxNode.Document),
        };
        Assert.Equal(new Rectangle(0, 0, 200, 18), FastRegionSelectorDialog.PreferredRung(chain));
    }

    [Fact]
    public void FallsBackToTheInnermostRectWhenEverythingIsStructuralNoise()
    {
        var chain = new[]
        {
            Node(300, 20, AxNode.StaticText),
            Node(800, 600, AxNode.Client),
            Node(820, 640, AxNode.Window),
        };
        Assert.Equal(new Rectangle(0, 0, 300, 20), FastRegionSelectorDialog.PreferredRung(chain));
    }

    [Fact]
    public void EmptyChainHasNoAnswer() =>
        Assert.Null(FastRegionSelectorDialog.PreferredRung(Array.Empty<AxNode>()));
}
