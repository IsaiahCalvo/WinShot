using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Auto-Scroll drives whatever is under the pointer, so it must only inject while the
/// pointer is over the captured region — that is both what makes the wheel reach the right
/// window and how the user pauses a run (park the pointer elsewhere).
/// </summary>
public class ScrollAutoScrollGateTests
{
    private static readonly SD.Rectangle Region = new(100, 200, 400, 300);

    [Theory]
    [InlineData(100, 200)]   // top-left corner is inside
    [InlineData(300, 350)]
    [InlineData(499, 499)]   // bottom-right pixel is inside
    public void PointerInsideTheRegion_MayInject(int x, int y)
        => Assert.True(ScrollAutoScrollGate.MayInject(Region, new SD.Point(x, y)));

    [Theory]
    [InlineData(99, 350)]
    [InlineData(500, 350)]   // right edge is exclusive
    [InlineData(300, 199)]
    [InlineData(300, 500)]   // bottom edge is exclusive
    [InlineData(-2000, -900)] // a monitor above and left of the primary
    public void PointerOutsideTheRegion_HoldsTheWheel(int x, int y)
        => Assert.False(ScrollAutoScrollGate.MayInject(Region, new SD.Point(x, y)));
}
