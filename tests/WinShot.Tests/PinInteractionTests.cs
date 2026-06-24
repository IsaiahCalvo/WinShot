using WinShot.Pin;
using Xunit;

namespace WinShot.Tests;

public class PinInteractionTests
{
    [Theory]
    [InlineData(1.0, 120, 1.1)]
    [InlineData(1.0, -120, 0.9090909090909091)]
    [InlineData(3.0, 120, 3.0)]
    [InlineData(0.2, -120, 0.2)]
    public void AdjustScale_UsesSharedWheelBounds(double current, int delta, double expected)
    {
        Assert.Equal(expected, PinInteraction.AdjustScale(current, delta), precision: 10);
    }

    [Theory]
    [InlineData(0.9, 120, 1.0)]
    [InlineData(0.9, -120, 0.8)]
    [InlineData(1.0, 120, 1.0)]
    [InlineData(0.3, -120, 0.3)]
    public void AdjustOpacity_UsesSharedWheelBounds(double current, int delta, double expected)
    {
        Assert.Equal(expected, PinInteraction.AdjustOpacity(current, delta), precision: 10);
    }

    [Theory]
    [InlineData(1.0, 0.85)]
    [InlineData(0.2, 0.17)]
    [InlineData(0.05, 0.1)]
    public void LockedOpacity_DimsWithoutBecomingInvisible(double current, double expected)
    {
        Assert.Equal(expected, PinInteraction.LockedOpacity(current), precision: 10);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 10)]
    public void NudgeStep_UsesShiftForLargerSteps(bool shift, int expected)
    {
        Assert.Equal(expected, PinInteraction.NudgeStep(shift));
    }
}
