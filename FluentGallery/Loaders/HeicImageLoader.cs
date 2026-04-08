using FluentGallery.Decoders;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace FluentGallery.Loaders;

/// <summary>
/// Image loader for HEIC/HEIF files.
///
/// <para>
/// <b>Preload strategy:</b> decodes HEIC → raw BGRA8 pixels via
/// <see cref="ImageDecoderPipeline"/> (Magick.NET / libheif, thread-safe) and stores the
/// <see cref="DecodedImageData"/> pixel buffer directly in the bounded preload cache.
/// No WIC is used during preloading: all N preloads run fully in parallel.
/// </para>
/// <para>
/// <b>Display:</b> BGRA8 pixels →
/// <see cref="SoftwareBitmap.CreateCopyFromBuffer"/> (no WIC, no <see cref="WicGate"/>) →
/// <see cref="SoftwareBitmap.Convert"/> to Premultiplied →
/// <see cref="SoftwareBitmapSource.SetBitmapAsync"/> on the UI thread.
/// The caller owns the returned <see cref="SoftwareBitmapSource"/> and must
/// <see cref="IDisposable.Dispose"/> it when switching photos.
/// </para>
/// <para>
/// <b>Thread safety:</b> all decode operations (Magick.NET) run on MTA thread-pool threads
/// without any global gate; <see cref="WicGate"/> is not used in this loader.
/// Only <see cref="SoftwareBitmapSource.SetBitmapAsync"/> runs on the UI thread (ASTA).
/// <see cref="LoadAsync"/> must be called from the UI thread.
/// <see cref="PreloadAsync"/> can be fire-and-forgotten from any thread.
/// </para>
/// <para>
/// <b>Memory:</b> cache stores raw BGRA8 pixels (~48 MB per 12 MP image vs ~7 MB for
/// the previous PNG-bytes approach). For the default 8-slot cache this is ~384 MB for
/// 12 MP images; real usage is proportional to actual image resolution.
/// </para>
/// </summary>
public sealed class HeicImageLoader : IImageLoader
{
    private static readonly HashSet<string> _heicExts =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    private readonly ImageDecoderPipeline    _pipeline;
    private readonly ILogger<HeicImageLoader> _logger;

    // BGRA8 pixel cache — accessed from Task.Run (thread pool), so all mutations
    // are protected by _cacheLock. Reads that might race a write also use the lock.
    private readonly Dictionary<string, DecodedImageData> _pixelCache =
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

            AddToCache(filePath, decoded);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "HeicImageLoader preload failed: {Path}", filePath); }
    }

    /// <inheritdoc/>
    /// Must be called from the UI thread (because of SetBitmapAsync).
    public async Task<LoadedImage?> LoadAsync(string filePath, CancellationToken ct,
        WicPriority priority = WicPriority.High)
    {
        // All pixel work runs on a thread-pool MTA thread.
        // Task.Run without ConfigureAwait(false) resumes on the UI SynchronizationContext
        // so SetBitmapAsync is safe.
#pragma warning disable CAC001
        var (softwareBitmap, w, h) = await Task.Run(async () =>
#pragma warning restore CAC001
        {
            // ── Step 1: get decoded pixels (cache hit or fresh decode) ────────
            DecodedImageData? decoded;
            lock (_cacheLock) { _pixelCache.TryGetValue(filePath, out decoded); }

            if (decoded is null)
            {
                decoded = await _pipeline
                    .TryDecodeAsync(filePath, 0, 0, ct, concurrentSafe: true)
                    .ConfigureAwait(false);
                if (decoded is null) return ((SoftwareBitmap?)null, 0, 0);
                AddToCache(filePath, decoded);
            }

            ct.ThrowIfCancellationRequested();

            int w = (int)decoded.Width;
            int h = (int)decoded.Height;

            // ── Step 2: BGRA8 pixels → SoftwareBitmap — no WicGate needed ────
            using var sbIgnore = SoftwareBitmap.CreateCopyFromBuffer(
                decoded.Pixels.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                w, h,
                BitmapAlphaMode.Ignore);

            var sb = SoftwareBitmap.Convert(
                sbIgnore,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);

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
            _pixelCache.Clear();
            _insertionOrder.Clear();
        }
        _logger.LogDebug("HeicImageLoader: pixel cache cleared");
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private void AddToCache(string path, DecodedImageData decoded)
    {
        int count; string first, last; long totalBytes;
        lock (_cacheLock)
        {
            _pixelCache[path] = decoded;
            _insertionOrder.Remove(path);
            _insertionOrder.Add(path);

            while (_insertionOrder.Count > MaxCacheSize)
            {
                var oldest = _insertionOrder[0];
                _insertionOrder.RemoveAt(0);
                _pixelCache.Remove(oldest);
            }

            count      = _insertionOrder.Count;
            first      = _insertionOrder[0];
            last       = _insertionOrder[^1];
            totalBytes = _pixelCache.Values.Sum(d => (long)d.Pixels.Length);
        }

        _logger.LogDebug(
            "HeicCache [{Count}/{Max}] First={First} Last={Last} Added={Added} TotalMB={TotalMB:F1}",
            count, MaxCacheSize,
            Path.GetFileName(first), Path.GetFileName(last),
            Path.GetFileName(path),
            totalBytes / (1024.0 * 1024.0));
    }
}
