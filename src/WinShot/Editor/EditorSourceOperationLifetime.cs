namespace WinShot.Editor;

/// <summary>
/// Keeps editor-owned bitmaps alive while background work borrows them. Closing prevents a new
/// root operation, waits for every in-flight/nested lease, then lets the window dispose all owned
/// bitmaps together.
/// </summary>
internal sealed class EditorSourceOperationLifetime
{
    private readonly object _gate = new();
    private int _active;
    private bool _closing;
    private TaskCompletionSource<object?>? _drained;

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_closing)
                throw new ObjectDisposedException(nameof(EditorSourceOperationLifetime));
            _active++;
        }
        return new Lease(this);
    }

    public IDisposable AcquireNested()
    {
        lock (_gate)
        {
            // A root lease must already cover the full effect/transform/load. Its refresh may
            // finish after close begins, but it cannot outlive that root operation.
            if (_active == 0)
                throw new InvalidOperationException("A nested source lease requires an active root operation.");
            _active++;
        }
        return new Lease(this);
    }

    public Task BeginClose()
    {
        lock (_gate)
        {
            _closing = true;
            if (_active == 0)
                return Task.CompletedTask;

            _drained ??= new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    private void Release()
    {
        TaskCompletionSource<object?>? drained = null;
        lock (_gate)
        {
            if (--_active == 0 && _closing)
                drained = _drained;
        }
        drained?.TrySetResult(null);
    }

    private sealed class Lease(EditorSourceOperationLifetime owner) : IDisposable
    {
        private EditorSourceOperationLifetime? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
