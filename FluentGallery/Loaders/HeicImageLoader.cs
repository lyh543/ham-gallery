using FluentGallery.Decoders;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluentGallery.Loaders;

/// <summary>
/// Image loader for HEIC/HEIF files.
///
/// <para>
/// <b>Preload strategy:</b> decodes HEIC → raw BGRA8 pixels via
/// <see cref="ImageDecoderPipeline"/>, then encodes to PNG bytes in memory.
/// PNG bytes are stored in the bounded preload cache (background-thread-safe,
/// ~7 MB per 12 MP image vs ~48 MB for raw BGRA8).
/// </para>
/// <para>
/// <b>Display:</b> PNG bytes → <see cref="SoftwareBitmap"/> (via <see cref="BitmapDecoder"/>
/// on a thread-pool MTA thread) → <see cref="SoftwareBitmapSource.SetBitmapAsync"/> on the
/// UI thread. The caller owns the returned <see cref="SoftwareBitmapSource"/> and must
/// <see cref="IDisposable.Dispose"/> it when switching photos.
/// </para>
/// <para>
/// <b>Thread safety:</b> all WIC operations (BitmapEncoder, BitmapDecoder) run on MTA
/// thread-pool threads and are serialised via <see cref="WicGate"/> to prevent concurrent
/// access crashes. Only <see cref="SoftwareBitmapSource.SetBitmapAsync"/> runs on the UI
/// thread (ASTA), as required by WinUI. <see cref="LoadAsync"/> must be called from the UI
/// thread. <see cref="PreloadAsync"/> can be fire-and-forgotten from any thread.
/// </para>
/// </summary>
public sealed class HeicImageLoader : IImageLoader
{
    private static readonly HashSet<string> _heicExts =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    private readonly ImageDecoderPipeline   _pipeline;
    private readonly ILogger<HeicImageLoader> _logger;

    // PNG bytes preload cache — accessed from Task.Run (thread pool), so all mutations
    // are protected by _cacheLock. Reads that might race a write also use the lock.
    private readonly Dictionary<string, byte[]> _pngCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _insertionOrder = [];
    private readonly object _cacheLock = new();

    /// <inheritdoc/>
    public int MaxCacheSize { get; set; } = 11;

    public HeicImageLoader(ImageDecoderPipeline pipeline, ILogger<HeicImageLoader> logger)
    {
        _pipeline = pipeline;
        _logger   = logger;
    }

    // ── IImageLoader ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool IsSupported(string extension) => _heicExts.Contains(extension);

    /// <inheritdoc/>
    public Task PreloadAsync(string filePath, CancellationToken ct)
    {
        lock (_cacheLock) { if (_pngCache.ContainsKey(filePath)) return Task.CompletedTask; }
        return PreloadInternalAsync(filePath, ct);
    }

    private async Task PreloadInternalAsync(string filePath, CancellationToken ct)
    {
        try
        {
            lock (_cacheLock) { if (_pngCache.ContainsKey(filePath)) return; }
            ct.ThrowIfCancellationRequested();

            var decoded = await _pipeline
                .TryDecodeAsync(filePath, 0, 0, ct, concurrentSafe: true)
                .ConfigureAwait(false);
            if (decoded is null) return;

            // WicGate serialises all WIC BitmapEncoder calls across threads.
            await WicGate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            byte[] bytes;
            try { bytes = await EncodeToPngBytesAsync(decoded, ct).ConfigureAwait(false); }
            finally { WicGate.Semaphore.Release(); }
            AddToCache(filePath, bytes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "HeicImageLoader preload failed: {Path}", filePath); }
    }

    /// <inheritdoc/>
    /// Must be called from the UI thread (because of SetBitmapAsync).
    public async Task<LoadedImage?> LoadAsync(string filePath, CancellationToken ct)
    {
        // All WIC work (BitmapEncoder + BitmapDecoder) runs on MTA thread-pool threads,
        // avoiding ASTA/MTA apartment-crossing COMExceptions.
        // Task.Run without ConfigureAwait(false) resumes on the UI SynchronizationContext
        // so SetBitmapAsync is safe.
#pragma warning disable CAC001
        var (softwareBitmap, w, h) = await Task.Run(async () =>
#pragma warning restore CAC001
        {
            // ── Step 1: get PNG bytes (cache hit or fresh encode) ─────────────
            byte[]? pngBytes;
            int w = 0, h = 0;
            lock (_cacheLock) { _pngCache.TryGetValue(filePath, out pngBytes); }

            if (pngBytes is null)
            {
                var decoded = await _pipeline
                    .TryDecodeAsync(filePath, 0, 0, ct, concurrentSafe: true)
                    .ConfigureAwait(false);
                if (decoded is null) return ((SoftwareBitmap?)null, 0, 0);

                await WicGate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
                try { pngBytes = await EncodeToPngBytesAsync(decoded, ct).ConfigureAwait(false); }
                finally { WicGate.Semaphore.Release(); }
                AddToCache(filePath, pngBytes);
                w = (int)decoded.Width;
                h = (int)decoded.Height;
            }

            // ── Step 2: PNG bytes → SoftwareBitmap (MTA, WIC-gated) ──────────
            await WicGate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            SoftwareBitmap sb;
            try
            {
                using var stream  = new MemoryStream(pngBytes).AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
                if (w == 0) w = (int)decoder.PixelWidth;
                if (h == 0) h = (int)decoder.PixelHeight;
                sb = await decoder
                    .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                    .AsTask(ct)
                    .ConfigureAwait(false);
            }
            finally { WicGate.Semaphore.Release(); }

            return (sb, w, h);
        }, ct);

        if (softwareBitmap is null) return null;
        ct.ThrowIfCancellationRequested();

        // ── Step 3: upload to GPU — must be on UI thread (ASTA) ──────────────
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(softwareBitmap);
        softwareBitmap.Dispose();

        return new LoadedImage(source, w, h);
    }

    /// <inheritdoc/>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _pngCache.Clear();
            _insertionOrder.Clear();
        }
        _logger.LogDebug("HeicImageLoader: PNG preload cache cleared");
    }

    // ── PNG encoding ──────────────────────────────────────────────────────────

    internal static async Task<byte[]> EncodeToPngBytesAsync(
        DecodedImageData decoded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var memRas  = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder
            .CreateAsync(BitmapEncoder.PngEncoderId, memRas)
            .AsTask(ct)
            .ConfigureAwait(false);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            decoded.Width,
            decoded.Height,
            decoded.DpiX,
            decoded.DpiY,
            decoded.Pixels);

        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

        memRas.Seek(0);
        var bytes = new byte[memRas.Size];
        using var reader = new DataReader(memRas);
        await reader.LoadAsync((uint)bytes.Length).AsTask(ct).ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private void AddToCache(string path, byte[] bytes)
    {
        int count; string first, last; long totalBytes;
        lock (_cacheLock)
        {
            _pngCache[path] = bytes;
            _insertionOrder.Remove(path);
            _insertionOrder.Add(path);

            while (_insertionOrder.Count > MaxCacheSize)
            {
                var oldest = _insertionOrder[0];
                _insertionOrder.RemoveAt(0);
                _pngCache.Remove(oldest);
            }

            count      = _insertionOrder.Count;
            first      = _insertionOrder[0];
            last       = _insertionOrder[^1];
            totalBytes = _pngCache.Values.Sum(b => (long)b.Length);
        }

        _logger.LogDebug(
            "HeicCache [{Count}/{Max}] First={First} Last={Last} Added={Added} TotalMB={TotalMB:F1}",
            count, MaxCacheSize,
            Path.GetFileName(first), Path.GetFileName(last),
            Path.GetFileName(path),
            totalBytes / (1024.0 * 1024.0));
    }
}
