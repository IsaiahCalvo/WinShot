using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// Runs post-capture readers after the editor has detached its visual tiles. History and
/// auto-copy are scheduled together but borrow the single authoritative GDI bitmap one at a
/// time. The editor keeps editing disabled until both complete.
/// </summary>
internal sealed class DirectEditCaptureHandoff
{
    private readonly SD.Bitmap _source;
    private readonly Func<SD.Bitmap, Task> _historyWork;
    private readonly Func<SD.Bitmap, Task>? _autoCopyWork;
    private readonly SemaphoreSlim _sourceGate = new(1, 1);

    public DirectEditCaptureHandoff(
        SD.Bitmap source,
        Func<SD.Bitmap, Task> historyWork,
        Func<SD.Bitmap, Task>? autoCopyWork)
    {
        _source = source;
        _historyWork = historyWork;
        _autoCopyWork = autoCopyWork;
    }

    public Task RunAsync()
    {
        // The clipboard takes the bitmap first. It is a quick CF_DIB write the user may be
        // about to paste; history is a PNG encode and a disk write. In the other order a
        // paste made right after the capture got the old clipboard.
        Task autoCopy = _autoCopyWork is null
            ? Task.CompletedTask
            : RunExclusiveAsync(_autoCopyWork);
        Task history = RunExclusiveAsync(_historyWork);
        return Task.WhenAll(history, autoCopy);
    }

    private async Task RunExclusiveAsync(Func<SD.Bitmap, Task> work)
    {
        await _sourceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await work(_source).ConfigureAwait(false);
        }
        finally
        {
            _sourceGate.Release();
        }
    }
}
