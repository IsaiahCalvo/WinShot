using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class EditorSourceOperationLifetimeTests
{
    [Fact]
    public async Task BeginClose_WaitsForEveryRootAndNestedLease()
    {
        var lifetime = new EditorSourceOperationLifetime();
        IDisposable root = lifetime.Acquire();

        Task drained = lifetime.BeginClose();
        IDisposable nestedRefresh = lifetime.AcquireNested();
        Assert.False(drained.IsCompleted);

        root.Dispose();
        Assert.False(drained.IsCompleted);
        nestedRefresh.Dispose();

        await drained;
        Assert.Throws<ObjectDisposedException>(() => lifetime.Acquire());
    }
}
