using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace WinShot.Core;

public static class CaptureService
{
    private static readonly object BitmapSourceWorkerGate = new();
    private static readonly SemaphoreSlim ClipboardGate = new(1, 1);
    private static BlockingCollection<BitmapSourceWorkItem>? _bitmapSourceWork;

    /// <summary>Bounds of all monitors combined, in physical screen pixels.</summary>
    public static Rectangle VirtualScreen => System.Windows.Forms.SystemInformation.VirtualScreen;

    public static Bitmap CaptureVirtualDesktop()
    {
        var total = Stopwatch.StartNew();
        long duplicationMs = 0;
        long wgcMs = 0;
        long bitBltMs = 0;
        Rectangle screenRect = VirtualScreen;
        if (ShouldUseFastScreenCapture(screenRect))
        {
            var step = Stopwatch.StartNew();
            bool captured = WindowsGraphicsCaptureCapture.TryCaptureRegion(screenRect, out Bitmap? wgcCapture);
            wgcMs = step.ElapsedMilliseconds;
            if (captured)
                return wgcCapture!;
        }

        if (ShouldUseDesktopDuplication(screenRect))
        {
            var step = Stopwatch.StartNew();
            bool captured = DesktopDuplicationCapture.TryCaptureRegion(screenRect, out Bitmap? desktopDuplicationCapture);
            duplicationMs = step.ElapsedMilliseconds;
            if (captured)
                return desktopDuplicationCapture!;
        }

        try
        {
            var copy = Stopwatch.StartNew();
            Bitmap bitmap = CaptureScreenRegionWithBitBlt(screenRect, includeLayeredWindows: false);
            bitBltMs = copy.ElapsedMilliseconds;
            if (total.ElapsedMilliseconds > 50)
            {
                Log.Info(
                    "Perf virtual desktop capture breakdown: " +
                    $"wgc={wgcMs} duplication={duplicationMs} rent=0 bitblt={bitBltMs} " +
                    $"total={total.ElapsedMilliseconds} ms size={screenRect.Width}x{screenRect.Height}");
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("BitBlt virtual desktop capture failed; falling back to CopyFromScreen", ex);
            return CaptureScreenRegionWithGraphics(screenRect);
        }
    }

    /// <summary>Captures a rectangle given in physical screen coordinates.</summary>
    public static Bitmap CaptureScreenRegion(Rectangle screenRect)
    {
        if (screenRect.Width < 1 || screenRect.Height < 1)
            throw new ArgumentException("Capture region is empty.", nameof(screenRect));

        if (ShouldUseFastScreenCapture(screenRect) &&
            WindowsGraphicsCaptureCapture.TryCaptureRegion(screenRect, out Bitmap? wgcCapture))
        {
            return wgcCapture!;
        }

        if (ShouldUseDesktopDuplication(screenRect) &&
            DesktopDuplicationCapture.TryCaptureRegion(screenRect, out Bitmap? desktopDuplicationCapture))
        {
            return desktopDuplicationCapture!;
        }

        if (ShouldUseDesktopDuplication(screenRect) && DesktopDuplicationCapture.IsTemporarilyUnavailable)
        {
            var total = Stopwatch.StartNew();
            try
            {
                var bitBlt = Stopwatch.StartNew();
                Bitmap bitmap = CaptureScreenRegionWithBitBlt(screenRect, includeLayeredWindows: false);
                long bitBltMs = bitBlt.ElapsedMilliseconds;
                if (total.ElapsedMilliseconds > 50)
                {
                    Log.Info(
                        "Perf screen region capture breakdown: " +
                        $"rent=0 bitblt={bitBltMs} " +
                        $"total={total.ElapsedMilliseconds} ms size={screenRect.Width}x{screenRect.Height}");
                }
                return bitmap;
            }
            catch (Exception ex)
            {
                Log.Error("SourceCopy screen capture failed; falling back to layered BitBlt", ex);
            }
        }

        try
        {
            return CaptureScreenRegionWithBitBlt(screenRect);
        }
        catch (Exception ex)
        {
            Log.Error("BitBlt screen capture failed; falling back to CopyFromScreen", ex);
            return CaptureScreenRegionWithGraphics(screenRect);
        }
    }

    internal static Bitmap CaptureScreenRegionWithoutLayeredWindows(Rectangle screenRect)
    {
        if (screenRect.Width < 1 || screenRect.Height < 1)
            throw new ArgumentException("Capture region is empty.", nameof(screenRect));

        return CaptureScreenRegionWithBitBlt(screenRect, includeLayeredWindows: false);
    }

    public static void ReleaseCaptureResources()
    {
        WindowsGraphicsCaptureCapture.ReleaseResources();
        DesktopDuplicationCapture.ReleaseResources();
    }

    private static bool ShouldUseDesktopDuplication(Rectangle screenRect)
    {
        return ShouldUseFastScreenCapture(screenRect);
    }

    private static bool ShouldUseFastScreenCapture(Rectangle screenRect)
    {
        const long LargeCapturePixels = 1_500_000;
        long pixels = (long)screenRect.Width * screenRect.Height;
        return pixels >= LargeCapturePixels;
    }

    private static Bitmap CaptureScreenRegionWithBitBlt(Rectangle screenRect, bool includeLayeredWindows = true)
    {
        var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppRgb);
        int rasterOp = includeLayeredWindows ? RasterOpSourceCopy | RasterOpCaptureBlt : RasterOpSourceCopy;
        bool dispose = false;
        try
        {
            using var g = Graphics.FromImage(bmp);
            IntPtr dest = g.GetHdc();
            IntPtr source = GetDC(IntPtr.Zero);
            try
            {
                if (!BitBlt(
                        dest,
                        0,
                        0,
                        screenRect.Width,
                        screenRect.Height,
                        source,
                        screenRect.X,
                        screenRect.Y,
                        rasterOp))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                if (source != IntPtr.Zero)
                    ReleaseDC(IntPtr.Zero, source);
                g.ReleaseHdc(dest);
            }

            return bmp;
        }
        catch
        {
            dispose = true;
            throw;
        }
        finally
        {
            if (dispose)
                bmp.Dispose();
        }
    }

    private static Bitmap CaptureScreenRegionWithGraphics(Rectangle screenRect)
    {
        var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(screenRect.X, screenRect.Y, 0, 0, screenRect.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static Bitmap Crop(Bitmap source, Rectangle region)
    {
        lock (source)
        {
            region.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (region.Width < 1 || region.Height < 1)
                throw new ArgumentException("Crop region is empty.", nameof(region));

            if (source.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format32bppRgb)
                return CropLocked32Bpp(source, region);

            return CropWithGraphics(source, region);
        }
    }

    private static Bitmap CropWithGraphics(Bitmap source, Rectangle region)
    {
        var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        bmp.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(source, new Rectangle(0, 0, region.Width, region.Height), region, GraphicsUnit.Pixel);
        return bmp;
    }

    private static Bitmap CropLocked32Bpp(Bitmap source, Rectangle region)
    {
        var crop = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        crop.SetResolution(source.HorizontalResolution, source.VerticalResolution);

        BitmapData? sourceData = null;
        BitmapData? cropData = null;
        bool disposeCrop = false;
        try
        {
            sourceData = source.LockBits(
                new Rectangle(0, 0, source.Width, source.Height),
                ImageLockMode.ReadOnly,
                source.PixelFormat);
            cropData = crop.LockBits(
                new Rectangle(0, 0, crop.Width, crop.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            nuint rowBytes = (nuint)(region.Width * 4);
            for (int y = 0; y < region.Height; y++)
            {
                IntPtr sourceRow = IntPtr.Add(
                    sourceData.Scan0,
                    (region.Y + y) * sourceData.Stride + region.X * 4);
                IntPtr cropRow = IntPtr.Add(cropData.Scan0, y * cropData.Stride);
                CopyMemory(cropRow, sourceRow, rowBytes);
            }

            return crop;
        }
        catch
        {
            disposeCrop = true;
            throw;
        }
        finally
        {
            if (cropData is not null)
                crop.UnlockBits(cropData);
            if (sourceData is not null)
                source.UnlockBits(sourceData);
            if (disposeCrop)
                crop.Dispose();
        }
    }

    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        lock (bmp)
        {
            if (bmp.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format32bppRgb)
                return ToBitmapSourceFromLocked32Bpp(bmp);
            return ToBitmapSourceViaHBitmap(bmp);
        }
    }

    private static BitmapSource ToBitmapSourceFromLocked32Bpp(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            // Always emit at 96 DPI so the BitmapSource's device-independent size equals its pixel
            // size. A capture/stitch bitmap inherits the system DPI (e.g. 120 on a 125% display),
            // which would make the WPF source report a SMALLER DIU size than the pixel-dimensioned
            // editor surface it fills — and a tall capture then renders only its top portion.
            var source = BitmapSource.Create(
                bmp.Width,
                bmp.Height,
                96,
                96,
                bmp.PixelFormat == PixelFormat.Format32bppPArgb ? WpfPixelFormats.Pbgra32 : WpfPixelFormats.Bgra32,
                null,
                data.Scan0,
                Math.Abs(data.Stride) * data.Height,
                data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static BitmapSource ToBitmapSourceViaHBitmap(Bitmap bmp)
    {
        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    public static BitmapSource ToBitmapSource(Bitmap bmp, int maxPixelWidth, int maxPixelHeight)
    {
        if (bmp.Width <= maxPixelWidth && bmp.Height <= maxPixelHeight)
            return ToBitmapSource(bmp);

        using var preview = CreateScaledBitmapSnapshot(bmp, maxPixelWidth, maxPixelHeight);
        return ToBitmapSource(preview);
    }

    public static Task<BitmapSource> ToBitmapSourceSnapshotAsync(Bitmap bmp)
        => RunBitmapSourceConversion(() =>
        {
            Bitmap copy = CloneBitmap(bmp);
            using (copy)
                return ToBitmapSource(copy);
        });

    public static Bitmap CloneBitmap(Bitmap bmp)
    {
        lock (bmp)
        {
            if (bmp.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format32bppRgb)
                return CloneLocked32Bpp(bmp);
            return (Bitmap)bmp.Clone();
        }
    }

    private static Bitmap CloneLocked32Bpp(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var clone = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        clone.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);

        BitmapData? sourceData = null;
        BitmapData? cloneData = null;
        bool disposeClone = false;
        try
        {
            sourceData = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
            cloneData = clone.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            nuint rowBytes = (nuint)(bmp.Width * 4);
            for (int y = 0; y < bmp.Height; y++)
            {
                IntPtr sourceRow = IntPtr.Add(sourceData.Scan0, y * sourceData.Stride);
                IntPtr cloneRow = IntPtr.Add(cloneData.Scan0, y * cloneData.Stride);
                CopyMemory(cloneRow, sourceRow, rowBytes);
            }

            return clone;
        }
        catch
        {
            disposeClone = true;
            throw;
        }
        finally
        {
            if (cloneData is not null)
                clone.UnlockBits(cloneData);
            if (sourceData is not null)
                bmp.UnlockBits(sourceData);
            if (disposeClone)
                clone.Dispose();
        }
    }

    public static System.Drawing.Size GetBitmapSize(Bitmap bmp)
    {
        lock (bmp)
            return new System.Drawing.Size(bmp.Width, bmp.Height);
    }

    public static Task<BitmapSource> ToBitmapSourceSnapshotAsync(Bitmap bmp, int maxPixelWidth, int maxPixelHeight)
        => RunBitmapSourceConversion(() =>
        {
            Bitmap copy = CreateBitmapSnapshot(bmp, maxPixelWidth, maxPixelHeight);
            using (copy)
                return ToBitmapSource(copy);
        });

    private static Task<BitmapSource> RunBitmapSourceConversion(Func<BitmapSource> convert)
    {
        var completion = new TaskCompletionSource<BitmapSource>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BitmapSourceWork.Add(new BitmapSourceWorkItem(convert, completion));
        return completion.Task;
    }

    private static Bitmap CreateBitmapSnapshot(Bitmap bmp, int maxPixelWidth, int maxPixelHeight)
    {
        lock (bmp)
        {
            if (bmp.Width <= maxPixelWidth && bmp.Height <= maxPixelHeight)
            {
                if (bmp.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format32bppRgb)
                    return CloneLocked32Bpp(bmp);
                return (Bitmap)bmp.Clone();
            }

            return CreateScaledBitmapSnapshotCore(bmp, maxPixelWidth, maxPixelHeight);
        }
    }

    private static Bitmap CreateScaledBitmapSnapshot(Bitmap bmp, int maxPixelWidth, int maxPixelHeight)
    {
        lock (bmp)
            return CreateScaledBitmapSnapshotCore(bmp, maxPixelWidth, maxPixelHeight);
    }

    private static Bitmap CreateScaledBitmapSnapshotCore(Bitmap bmp, int maxPixelWidth, int maxPixelHeight)
    {
        double scale = Math.Min(maxPixelWidth / (double)bmp.Width, maxPixelHeight / (double)bmp.Height);
        int width = Math.Max(1, (int)Math.Round(bmp.Width * scale));
        int height = Math.Max(1, (int)Math.Round(bmp.Height * scale));

        var preview = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        preview.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);

        bool disposePreview = false;
        try
        {
            using var g = Graphics.FromImage(preview);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(
                bmp,
                new Rectangle(0, 0, width, height),
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                GraphicsUnit.Pixel);

            return preview;
        }
        catch
        {
            disposePreview = true;
            throw;
        }
        finally
        {
            if (disposePreview)
                preview.Dispose();
        }
    }

    private static BlockingCollection<BitmapSourceWorkItem> BitmapSourceWork
    {
        get
        {
            lock (BitmapSourceWorkerGate)
            {
                if (_bitmapSourceWork is not null)
                    return _bitmapSourceWork;

                var queue = new BlockingCollection<BitmapSourceWorkItem>();
                var thread = new Thread(() => RunBitmapSourceWorker(queue))
                {
                    IsBackground = true,
                    Name = "WinShot BitmapSource Worker",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                _bitmapSourceWork = queue;
                return queue;
            }
        }
    }

    /// <summary>
    /// Disposes <paramref name="bmp"/> AFTER every conversion currently queued on the
    /// bitmap-source worker has completed. Owners whose bitmap may still be referenced by an
    /// in-flight <see cref="ToBitmapSourceSnapshotAsync(Bitmap)"/> call (e.g. a window closing
    /// right after kicking off its image load) must dispose through this instead of directly —
    /// a direct Dispose races the worker's Clone and produces "Parameter is not valid".
    /// </summary>
    public static void DisposeAfterPendingConversions(Bitmap bmp) =>
        BitmapSourceWork.Add(new BitmapSourceWorkItem(
            () => { bmp.Dispose(); return null!; },
            new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously)));

    private static void RunBitmapSourceWorker(BlockingCollection<BitmapSourceWorkItem> queue)
    {
        foreach (var item in queue.GetConsumingEnumerable())
        {
            try
            {
                item.Completion.SetResult(item.Convert());
            }
            catch (Exception ex)
            {
                item.Completion.SetException(ex);
            }
        }
    }

    private sealed record BitmapSourceWorkItem(
        Func<BitmapSource> Convert,
        TaskCompletionSource<BitmapSource> Completion);

    /// <summary>Puts the image on the clipboard. Manual copy includes PNG for apps that prefer it.</summary>
    public static void CopyToClipboard(Bitmap bmp, bool includePng = true)
    {
        if (!includePng)
        {
            CopyDibToClipboard(bmp);
            return;
        }

        CopyWpfImageToClipboard(bmp, includePng);
    }

    private static void CopyWpfImageToClipboard(Bitmap bmp, bool includePng)
    {
        var data = new DataObject();
        data.SetImage(ToBitmapSource(bmp));
        MemoryStream? pngStream = null;
        try
        {
            if (includePng)
            {
                pngStream = new MemoryStream();
                bmp.Save(pngStream, ImageFormat.Png);
                pngStream.Position = 0;
                data.SetData("PNG", pngStream, autoConvert: false);
            }

            int maxAttempts = includePng ? 9 : 80;
            int retryDelayMs = includePng ? 75 : 125;

            // The clipboard can be transiently locked by another process; auto-copy can wait longer.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    Clipboard.Flush();
                    return;
                }
                catch (COMException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }
        }
        finally
        {
            pngStream?.Dispose();
        }
    }

    private static void CopyDibToClipboard(Bitmap bmp)
    {
        IntPtr dibHandle = CreateDibGlobalMemory(bmp);
        bool transferred = false;
        Win32Exception? lastError = null;
        try
        {
            const int maxAttempts = 4;
            const int retryDelayMs = 60;
            for (int attempt = 0; ; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        if (!EmptyClipboard())
                            throw new Win32Exception(Marshal.GetLastWin32Error());

                        if (SetClipboardData(ClipboardFormatDib, dibHandle) == IntPtr.Zero)
                            throw new Win32Exception(Marshal.GetLastWin32Error());

                        transferred = true;
                        return;
                    }
                    catch (Win32Exception ex) when (attempt < maxAttempts && IsTransientClipboardError(ex))
                    {
                        lastError = ex;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    lastError = error != 0
                        ? new Win32Exception(error)
                        : new Win32Exception("Clipboard is busy.");
                }

                if (attempt >= maxAttempts)
                    throw lastError ?? new Win32Exception("Clipboard is busy.");

                Thread.Sleep(retryDelayMs);
            }
        }
        finally
        {
            if (!transferred && dibHandle != IntPtr.Zero)
                GlobalFree(dibHandle);
        }
    }

    private static bool IsTransientClipboardError(Win32Exception ex) =>
        ex.NativeErrorCode is 0 or 5 or 1418;

    private static IntPtr CreateDibGlobalMemory(Bitmap bmp)
    {
        lock (bmp)
        {
            if (bmp.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format32bppRgb)
                return CreateDibGlobalMemoryFromLocked32Bpp(bmp);

            using var copy = ConvertTo32BppSnapshot(bmp);
            return CreateDibGlobalMemoryFromLocked32Bpp(copy);
        }
    }

    private static Bitmap ConvertTo32BppSnapshot(Bitmap bmp)
    {
        var copy = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        copy.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);
        using var g = Graphics.FromImage(copy);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        g.SmoothingMode = SmoothingMode.None;
        g.DrawImageUnscaled(bmp, 0, 0);
        return copy;
    }

    private static IntPtr CreateDibGlobalMemoryFromLocked32Bpp(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData? sourceData = null;
        IntPtr memory = IntPtr.Zero;
        IntPtr pointer = IntPtr.Zero;
        bool lockedMemory = false;
        bool releaseMemory = true;
        try
        {
            sourceData = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
            int rowBytes = checked(bmp.Width * 4);
            int imageBytes = checked(rowBytes * bmp.Height);
            int totalBytes = checked(BitmapInfoHeaderSize + imageBytes);

            memory = GlobalAlloc(GlobalMoveable | GlobalZeroInit, (UIntPtr)totalBytes);
            if (memory == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            lockedMemory = true;

            Marshal.WriteInt32(pointer, 0, BitmapInfoHeaderSize);
            Marshal.WriteInt32(pointer, 4, bmp.Width);
            Marshal.WriteInt32(pointer, 8, bmp.Height);
            Marshal.WriteInt16(pointer, 12, 1);
            Marshal.WriteInt16(pointer, 14, 32);
            Marshal.WriteInt32(pointer, 16, BitmapCompressionRgb);
            Marshal.WriteInt32(pointer, 20, imageBytes);
            Marshal.WriteInt32(pointer, 24, 0);
            Marshal.WriteInt32(pointer, 28, 0);
            Marshal.WriteInt32(pointer, 32, 0);
            Marshal.WriteInt32(pointer, 36, 0);

            for (int destinationY = 0; destinationY < bmp.Height; destinationY++)
            {
                int sourceY = bmp.Height - 1 - destinationY;
                IntPtr sourceRow = GetBitmapRowPointer(sourceData, sourceY, bmp.Height);
                IntPtr destinationRow = IntPtr.Add(pointer, BitmapInfoHeaderSize + destinationY * rowBytes);
                CopyMemory(destinationRow, sourceRow, (nuint)rowBytes);
            }

            releaseMemory = false;
            return memory;
        }
        finally
        {
            if (lockedMemory)
                GlobalUnlock(memory);
            if (sourceData is not null)
                bmp.UnlockBits(sourceData);
            if (releaseMemory && memory != IntPtr.Zero)
                GlobalFree(memory);
        }
    }

    private static IntPtr GetBitmapRowPointer(BitmapData data, int y, int height)
    {
        if (data.Stride >= 0)
            return IntPtr.Add(data.Scan0, y * data.Stride);

        return IntPtr.Add(data.Scan0, (height - 1 - y) * -data.Stride);
    }

    public static Task CopyToClipboardAsync(Bitmap bmp, bool takeOwnership = false, bool includePng = true)
    {
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Bitmap? clipboardBitmap = null;
            try
            {
                clipboardBitmap = takeOwnership ? bmp : CloneBitmap(bmp);
                ClipboardGate.Wait();
                try
                {
                    CopyToClipboard(clipboardBitmap, includePng);
                }
                finally
                {
                    ClipboardGate.Release();
                }
                completion.SetResult(null);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                clipboardBitmap?.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "WinShot Clipboard Copy",
        };
        thread.SetApartmentState(ApartmentState.STA);

        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            if (takeOwnership)
                bmp.Dispose();
            completion.SetException(ex);
        }

        return completion.Task;
    }

    public static Task SetTextToClipboardAsync(string text)
    {
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        Clipboard.SetText(text);
                        completion.SetResult(null);
                        return;
                    }
                    catch (COMException) when (attempt < 3)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "WinShot Clipboard Text Copy",
        };
        thread.SetApartmentState(ApartmentState.STA);

        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }

        return completion.Task;
    }

    public static string DefaultFileName(string extension) =>
        $"WinShot {DateTime.Now:yyyy-MM-dd 'at' HH.mm.ss}.{extension}";

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const uint ClipboardFormatDib = 8;
    private const uint GlobalMoveable = 0x0002;
    private const uint GlobalZeroInit = 0x0040;
    private const int BitmapInfoHeaderSize = 40;
    private const int BitmapCompressionRgb = 0;

    private const int RasterOpSourceCopy = 0x00CC0020;
    private const int RasterOpCaptureBlt = 0x40000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSource,
        int xSource,
        int ySource,
        int rasterOp);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, nuint length);
}
