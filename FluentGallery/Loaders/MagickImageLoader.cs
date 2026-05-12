using FluentGallery.Decoders;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace FluentGallery.Loaders;

/// <summary>
/// Universal fallback image loader backed by <see cref="ImageDecoderPipeline"/>
/// (Magick.NET / libheif for HEIC; WIC standard codecs for JPEG, PNG, etc.).
/// <para>
/// <b>When to use:</b> handles any format not covered by <see cref="WicImageLoader"/>.
/// In practice this means HEIC/HEIF and any other format the XAML <c>BitmapImage</c>
/// cannot open natively.  <see cref="IsSupported"/> returns <c>true</c> for every
/// extension so that this loader acts as a catch-all fallback in routing logic.
/// </para>
/// <para>
/// <b>Preload strategy:</b> decodes to raw BGRA8 pixels via
/// <see cref="ImageDecoderPipeline"/> (<c>concurrentSafe: true</c>) and stores the
/// <see cref="DecodedImageData"/> pixel buffer directly in the bounded preload cache.
/// All N preloads run fully in parallel — no serialisation gate is needed because
/// the pipeline skips any non-concurrent-safe codec (e.g. WIC HEIC) when
/// <c>concurrentSafe</c> is set.
/// </para>
/// <para>
/// <b>Display:</b> BGRA8 pixels →
/// <see cref="SoftwareBitmap.CreateCopyFromBuffer"/> →
/// <see cref="SoftwareBitmap.Convert"/> (Premultiplied) →
/// <see cref="SoftwareBitmapSource.SetBitmapAsync"/> on the UI thread.
/// The returned <see cref="LoadedImage.Source"/> is a <see cref="SoftwareBitmapSource"/>
/// (<see cref="IDisposable"/>); the caller must call
/// <see cref="IDisposable.Dispose"/> (via <c>DeferDispose</c>) when done.
/// </para>
/// <para>
/// <b>Thread safety:</b> all decode operations run on MTA thread-pool threads without
/// any global serialisation gate.  Only <see cref="SoftwareBitmapSource.SetBitmapAsync"/>
/// runs on the UI thread (ASTA).  <see cref="LoadAsync"/> must be called from the UI
/// thread; <see cref="PreloadAsync"/> can be fire-and-forgotten from any thread.
/// </para>
/// </summary>
public sealed class MagickImageLoader : IImageLoader
{
    private readonly ImageDecoderPipeline      _pipeline;
    private readonly ILogger<MagickImageLoader> _logger;

    // BGRA8 pixel cache — accessed from Task.Run (thread pool), so all mutations
    // are protected by _cacheLock. Reads that might race a write also use the lock.
    private readonly Dictionary<string, DecodedImageData> _pixelCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _insertionOrder = [];
    private readonly object _cacheLock = new();

    /// <inheritdoc/>
    public int MaxCacheSize { get; set; } = 11;

    public MagickImageLoader(ImageDecoderPipeline pipeline, ILogger<MagickImageLoader> logger)
    {
        _pipeline = pipeline;
        _logger   = logger;
    }

    // ── IImageLoader ──────────────────────────────────────────────────────────

    /// <summary>
    /// Always returns <c>true</c> — this loader is a catch-all fallback.
    /// Routing logic in the caller (e.g. <c>PhotoDetailPage</c>) should try
    /// higher-priority loaders (e.g. <see cref="WicImageLoader"/>) first.
    /// </summary>
    public bool IsSupported(string extension) => true;

    /// <inheritdoc/>
    public Task PreloadAsync(string filePath, CancellationToken ct)
    {
        lock (_cacheLock) { if (_pixelCache.ContainsKey(filePath)) return Task.CompletedTask; }
        return PreloadInternalAsync(filePath, ct);
    }

    private async Task PreloadInternalAsync(string filePath, CancellationToken ct)
    {
        try
        {
            lock (_cacheLock) { if (_pixelCache.ContainsKey(filePath)) return; }
            ct.ThrowIfCancellationRequested();

            var decoded = await _pipeline
                .TryDecodeAsync(filePath, 0, 0, ct, concurrentSafe: true)
                .ConfigureAwait(false);
            if (decoded is null) return;
            ct.ThrowIfCancellationRequested();

            AddToCache(filePath, decoded);
            decoded = null; // Release reference from async state machine
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "MagickImageLoader preload failed: {Path}", filePath); }
    }

    /// <inheritdoc/>
    /// Must be called from the UI thread (because of SetBitmapAsync).
    public async Task<LoadedImage?> LoadAsync(string filePath, CancellationToken ct)
    {
#pragma warning disable CAC001
        var (softwareBitmap, w, h) = await Task.Run(async () =>
#pragma warning restore CAC001
        {
            DecodedImageData? decoded;
            bool cacheHit;
            lock (_cacheLock)
            {
                cacheHit = _pixelCache.TryGetValue(filePath, out decoded);
                if (cacheHit)
                {
                    _pixelCache.Remove(filePath);
                    _insertionOrder.Remove(filePath);
                }
            }

            if (decoded is null)
            {
                decoded = await _pipeline
                    .TryDecodeAsync(filePath, 0, 0, ct, concurrentSafe: true)
                    .ConfigureAwait(false);
                if (decoded is null) return ((SoftwareBitmap?)null, 0, 0);
            }

            ct.ThrowIfCancellationRequested();

            int w = (int)decoded.Width;
            int h = (int)decoded.Height;

            // HEIC photos are always fully opaque (alpha=255), so straight and
            // premultiplied BGRA are identical.  Creating with Premultiplied directly
            // avoids a redundant SoftwareBitmap.Convert copy (~48 MB native).
            var sb = SoftwareBitmap.CreateCopyFromBuffer(
                decoded.Pixels.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                w, h,
                BitmapAlphaMode.Premultiplied);
            decoded = null; // Release LOH byte[] reference before awaiting

            return (sb, w, h);
        }, ct);

        if (softwareBitmap is null) return null;
        ct.ThrowIfCancellationRequested();

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(softwareBitmap);
        softwareBitmap.Dispose();

        return new LoadedImage(source, w, h);
    }

    /// <inheritdoc/>
    /// For Magick-decoded formats the preview path already skips non-preloaded images,
    /// so this delegates to <see cref="LoadAsync"/> (pixel cache is not consumed by load).
    public Task<LoadedImage?> LoadForPreviewAsync(string filePath, CancellationToken ct)
        => LoadAsync(filePath, ct);

    /// <inheritdoc/>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _pixelCache.Clear();
            _insertionOrder.Clear();
        }
    }

    /// <inheritdoc/>
    public void InvalidatePath(string filePath)
    {
        lock (_cacheLock)
        {
            _pixelCache.Remove(filePath);
            _insertionOrder.Remove(filePath);
        }
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private void AddToCache(string path, DecodedImageData decoded)
    {
        int count; string first, last; long totalBytes; long evictedBytes = 0;
        lock (_cacheLock)
        {
            _pixelCache[path] = decoded;
            _insertionOrder.Remove(path);
            _insertionOrder.Add(path);

            // Evict by count
            while (_insertionOrder.Count > MaxCacheSize)
            {
                var oldest = _insertionOrder[0];
                _insertionOrder.RemoveAt(0);
                if (_pixelCache.Remove(oldest, out var evicted))
                    evictedBytes += evicted.Pixels.Length;
            }

            // Evict by memory budget (~200 MB for BGRA8 pixel buffers)
            const long MaxCacheBytes = 200L * 1024 * 1024;
            while (_insertionOrder.Count > 1)
            {
                long total = _pixelCache.Values.Sum(d => (long)d.Pixels.Length);
                if (total <= MaxCacheBytes) break;
                var oldest = _insertionOrder[0];
                _insertionOrder.RemoveAt(0);
                if (_pixelCache.Remove(oldest, out var evicted2))
                    evictedBytes += evicted2.Pixels.Length;
            }

            count      = _insertionOrder.Count;
            first      = _insertionOrder[0];
            last       = _insertionOrder[^1];
            totalBytes = _pixelCache.Values.Sum(d => (long)d.Pixels.Length);
        }


    }
}
