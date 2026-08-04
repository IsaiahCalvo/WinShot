using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class DirectEditCaptureHandoffTests
{
    [Fact]
    public async Task RunAsync_SchedulesBothBorrowers_ButNeverSharesTheBitmapConcurrently()
    {
        using var source = new SD.Bitmap(64, 128);
        int activeBorrowers = 0;
        int maximumBorrowers = 0;
        int completedBorrowers = 0;

        async Task BorrowAsync(SD.Bitmap borrowed)
        {
            Assert.Same(source, borrowed);
            int active = Interlocked.Increment(ref activeBorrowers);
            InterlockedExtensions.Max(ref maximumBorrowers, active);
            try
            {
                await Task.Delay(40);
                Interlocked.Increment(ref completedBorrowers);
            }
            finally
            {
                Interlocked.Decrement(ref activeBorrowers);
            }
        }

        var handoff = new DirectEditCaptureHandoff(source, BorrowAsync, BorrowAsync);

        await handoff.RunAsync();

        Assert.Equal(2, completedBorrowers);
        Assert.Equal(1, maximumBorrowers);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int current = Volatile.Read(ref target);
            while (value > current)
            {
                int observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
