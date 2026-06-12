using System.Collections.Concurrent;
using System.IO;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Recording;

/// <summary>
/// Captures a screen region on a timer and streams the frames to a
/// <see cref="GifEncoder"/> on a dedicated thread. The frame queue is bounded:
/// when encoding falls behind, frames are dropped instead of buffered, so
/// memory stays flat for arbitrarily long recordings.
/// </summary>
public sealed class GifRecorder
{
    private readonly SD.Rectangle _screenRect;
    private readonly int _fps;
    private readonly string _outputPath;
    private readonly BlockingCollection<SD.Bitmap> _frames = new(boundedCapacity: 8);
    private readonly object _gate = new();

    private Thread? _encoderThread;
    private Timer? _timer;
    private int _ticking;
    private volatile bool _stopped;
    private volatile bool _failed;
    private int _framesEncoded;

    public GifRecorder(SD.Rectangle screenRect, int fps, string outputPath)
    {
        _screenRect = screenRect;
        _fps = Math.Clamp(fps, 1, 30);
        _outputPath = outputPath;
    }

    public void Start()
    {
        _encoderThread = new Thread(EncodeLoop) { IsBackground = true, Name = "WinShot GIF encoder" };
        _encoderThread.Start();
        _timer = new Timer(OnTick, null, 0, 1000 / _fps);
    }

    private void OnTick(object? state)
    {
        if (_stopped || _failed) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return; // skip overlapping ticks
        try
        {
            var bmp = CaptureService.CaptureScreenRegion(_screenRect);
            lock (_gate)
            {
                if (_frames.IsAddingCompleted || !_frames.TryAdd(bmp))
                    bmp.Dispose(); // encoder is behind (or we are stopping) — drop the frame
            }
        }
        catch (Exception ex)
        {
            Log.Error("GIF frame capture failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void EncodeLoop()
    {
        try
        {
            using var stream = new FileStream(_outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            using var encoder = new GifEncoder(stream, _screenRect.Width, _screenRect.Height, _fps);
            foreach (var frame in _frames.GetConsumingEnumerable())
            {
                using (frame)
                    encoder.AddFrame(frame);
                _framesEncoded++;
            }
            encoder.Finish();
        }
        catch (Exception ex)
        {
            _failed = true;
            Log.Error("GIF encoding failed", ex);
        }
    }

    /// <summary>
    /// Stops capturing, drains the queue, and blocks until the file is
    /// finalized. Returns false if encoding failed or no frames were written.
    /// Call from a background thread — this can take a few seconds.
    /// </summary>
    public bool Stop()
    {
        _stopped = true;
        if (_timer is not null)
        {
            // Dispose(WaitHandle) waits for any in-flight tick callback.
            using var drained = new ManualResetEvent(false);
            if (_timer.Dispose(drained))
                drained.WaitOne(TimeSpan.FromSeconds(2));
            _timer = null;
        }
        lock (_gate)
        {
            if (!_frames.IsAddingCompleted)
                _frames.CompleteAdding();
        }
        _encoderThread?.Join(TimeSpan.FromSeconds(30));
        while (_frames.TryTake(out var leftover))
            leftover.Dispose(); // only non-empty if the encoder bailed out early
        return !_failed && _framesEncoded > 0;
    }
}
