using WinShot.Scrolling;
using Xunit;

namespace WinShot.Tests;

public class ScrollCalibrationTests
{
    [Fact]
    public void VerticalAutoRecalibration_SubtractsHeaderAndFooter_AndPreservesOverlap()
    {
        ScrollCalibrationResult result = ScrollCalibration.Update(previousPixelsPerNotch: 0,
            measuredOffset: 30, sentNotches: 1, viewportLength: 400,
            leadingBand: 100, trailingBand: 100);

        Assert.Equal(200, result.MovingLength);
        Assert.Equal(176, result.MaxSafeMovement);
        Assert.Equal(30, result.PixelsPerNotch);
        Assert.Equal(4, result.Notches);
        Assert.Equal(120, result.EstimatedMovement);
        Assert.True(result.CanPreserveOverlap);
        Assert.True(result.MovingLength - result.EstimatedMovement >= ScrollCalibration.RequiredOverlap);

        // Rejected formula used only the footer: 0.6 * 300 / 30 = 6 notches, leaving 20px.
        Assert.True(result.Notches < 6);
    }

    [Fact]
    public void HorizontalAutoRecalibration_SubtractsLeftAndRight_AndPreservesOverlap()
    {
        ScrollCalibrationResult result = ScrollCalibration.Update(previousPixelsPerNotch: 0,
            measuredOffset: 30, sentNotches: 1, viewportLength: 400,
            leadingBand: 120, trailingBand: 100);

        Assert.Equal(180, result.MovingLength);
        Assert.Equal(156, result.MaxSafeMovement);
        Assert.Equal(4, result.Notches);
        Assert.Equal(120, result.EstimatedMovement);
        Assert.True(result.CanPreserveOverlap);
        Assert.True(result.MovingLength - result.EstimatedMovement >= ScrollCalibration.RequiredOverlap);

        // Rejected formula used all 400px: it would request the eight-notch cap (240px).
        Assert.True(result.Notches < 8);
    }

    [Fact]
    public void AutoRecalibration_SmoothsMeasurementWithoutBreakingTheSafeCap()
    {
        ScrollCalibrationResult result = ScrollCalibration.Update(previousPixelsPerNotch: 40,
            measuredOffset: 60, sentNotches: 2, viewportLength: 360,
            leadingBand: 70, trailingBand: 80);

        Assert.Equal(35, result.PixelsPerNotch);
        Assert.True(result.EstimatedMovement <= result.MaxSafeMovement);
        Assert.True(result.MovingLength - result.EstimatedMovement >= ScrollCalibration.RequiredOverlap);
    }

    [Fact]
    public void AutoRecalibration_ReportsWhenEvenOneNotchCannotPreserveOverlap()
    {
        ScrollCalibrationResult result = ScrollCalibration.Update(previousPixelsPerNotch: 0,
            measuredOffset: 80, sentNotches: 1, viewportLength: 120,
            leadingBand: 30, trailingBand: 30);

        Assert.False(result.CanPreserveOverlap);
        Assert.Equal(0, result.Notches);
        Assert.Equal(0, result.EstimatedMovement);
        Assert.True(result.EstimatedMovement <= result.MaxSafeMovement);
    }

    [Fact]
    public void AutoRecalibration_StopsWhenTheMovingBodyIsSmallerThanRequiredOverlap()
    {
        ScrollCalibrationResult result = ScrollCalibration.Update(previousPixelsPerNotch: 0,
            measuredOffset: 1, sentNotches: 1, viewportLength: 100,
            leadingBand: 40, trailingBand: 40);

        Assert.Equal(20, result.MovingLength);
        Assert.Equal(0, result.MaxSafeMovement);
        Assert.False(result.CanPreserveOverlap);
        Assert.Equal(0, result.Notches);
    }
}
